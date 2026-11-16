using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigStoreCancellationTests
{
    [Test]
    [Arguments("config", false)]
    [Arguments("donor", false)]
    [Arguments("copy", false)]
    [Arguments("copy", true)]
    [Arguments("write", false)]
    public async Task ReadForWriteAsync_Should_Propagate_The_Cancellation_Instance_With_No_Later_Call(string stage, bool readDoor)
    {
        var scenario = new ServiceConfigScenario("beta");
        await scenario.SeedDonorAsync("unknown-member");
        using var caller = new CancellationTokenSource();
        var cause = new OperationCanceledException(caller.Token);
        if (stage is "config" or "donor")
        {
            var path = stage == "config" ? scenario.Paths.ConfigFile! : scenario.Paths.LegacyConfigFile!;
            scenario.FileSystem.TryReadAllBytesAsync(path, Arg.Any<CancellationToken>()).Returns(Task.FromException<byte[]?>(cause));
        }
        else if (stage == "copy")
        {
            scenario.FileSystem.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<bool>(cause));
        }
        else
        {
            scenario.FileSystem.WriteOwnerOnlyAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(cause));
        }
        scenario.ClearCalls();

        var failure = await Assert.That(async () =>
        {
            if (readDoor)
            {
                _ = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, caller.Token);
            }
            else
            {
                _ = await new ServiceConfigEditor(scenario.Store).GetOrCreatePasswordAsync(scenario.Options, caller.Token);
            }
        }).Throws<OperationCanceledException>();

        await Assert.That(failure).IsSameReferenceAs(cause);
        var expected = new List<string> { "TryReadAllBytesAsync:" + scenario.Paths.ConfigFile };
        if (stage != "config")
        {
            expected.AddRange(["Exists:" + scenario.Paths.ConfigFile, "TryReadAllBytesAsync:" + scenario.Paths.LegacyConfigFile]);
        }
        if (stage is "copy" or "write")
        {
            expected.Add("TryCreateExclusiveAsync:" + scenario.Paths.ConfigFile);
        }
        if (stage == "write")
        {
            expected.AddRange(["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile,
                "CreateDirectory:" + scenario.Files.Path.GetDirectoryName(scenario.Paths.ConfigFile),
                "WriteOwnerOnlyAsync:" + scenario.Paths.ConfigFile + ".12345.tmp"]);
        }
        await Assert.That(scenario.Calls).IsEquivalentTo(expected, CollectionOrdering.Matching);
        foreach (var call in scenario.FileSystem.ReceivedCalls().Where(static call => call.GetMethodInfo().Name.EndsWith("Async", StringComparison.Ordinal)))
        {
            await Assert.That(call.GetArguments()[^1]).IsEqualTo(caller.Token);
        }
    }

    [Test]
    public async Task WriteAsync_Should_Observe_Cancellation_After_Directory_Creation() =>
        await ObserveSynchronousCancellationAsync(rename: false);

    [Test]
    public async Task WriteAsync_Should_Complete_When_Cancelled_During_A_Successful_Rename() =>
        await ObserveSynchronousCancellationAsync(rename: true);

    private static async Task ObserveSynchronousCancellationAsync(bool rename)
    {
        using var caller = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new ServiceConfigScenario();
        var pause = () =>
        {
            entered.SetResult();
            release.Wait();
        };
        if (rename)
        {
            scenario.Rename = (source, destination) =>
            {
                scenario.Adapter.Rename(source, destination);
                pause();
            };
        }
        else
        {
            scenario.FileSystem.When(files => files.CreateDirectory(Arg.Any<string>())).Do(_ => pause());
        }
        scenario.ClearCalls();
        var token = caller.Token;
        // A synchronous seam blocks only its dedicated worker, never the thread pool that cancels it.
        var operation = Task.Factory.StartNew(() => scenario.Store.WriteAsync(scenario.Paths, new ServiceConfigDocument(), token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            await Assert.That(await Task.WhenAny(operation, entered.Task).WaitAsync(TimeSpan.FromSeconds(10))).IsSameReferenceAs(entered.Task);
            await caller.CancelOnWorkerAsync();
            release.Set();
            if (rename)
            {
                await operation;
                await Assert.That(await scenario.ReadTextAsync(scenario.Paths.ConfigFile!)).IsEqualTo("{}\n");
                var write = scenario.FileSystem.ReceivedCalls().Single(static call => call.GetMethodInfo().Name == "WriteOwnerOnlyAsync");
                await Assert.That(write.GetArguments()[2]).IsEqualTo(token);
            }
            else
            {
                var error = await Assert.That(operation).Throws<OperationCanceledException>();
                await Assert.That(error).IsNotNull();
                await Assert.That(error.CancellationToken).IsEqualTo(token);
                await Assert.That(scenario.Calls).IsEquivalentTo(
                    ["CreateDirectory:" + scenario.Files.Path.GetDirectoryName(scenario.Paths.ConfigFile)], CollectionOrdering.Matching);
            }
        }
        finally
        {
            release.Set();
            _ = await Task.WhenAny(operation).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
