using System.Text;
using NSubstitute;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigEditorCancellationTests
{
    [Test]
    [Arguments("stop")]
    [Arguments("read")]
    public async Task SetHostnameAsync_Should_Preserve_Stop_And_Second_Read_Cancellation(string stage)
    {
        var scenario = new ServiceConfigScenario();
        var initial = scenario.Initial("all-members")!;
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, initial);
        using var caller = new CancellationTokenSource();
        var cause = new OperationCanceledException(caller.Token);
        if (stage == "stop")
        {
            scenario.Stop = (options, token) => Task.FromException(cause);
        }
        else
        {
            scenario.FileSystem.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(initial)), Task.FromException<byte[]?>(cause));
        }
        scenario.ClearCalls();

        var failure = await Assert.That(async () => await OpenCodeServiceConfiguration.SetHostnameWithSeamsAsync(
            "requested", scenario.Options, scenario.Editor, caller.Token)).Throws<OperationCanceledException>();

        await Assert.That(failure).IsSameReferenceAs(cause);
        await Assert.That(scenario.StopTokens).IsEquivalentTo([caller.Token], CollectionOrdering.Matching);
        string[] calls = stage == "stop" ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile]
            : ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "TryReadAllBytesAsync:" + scenario.Paths.ConfigFile];
        await Assert.That(scenario.Calls).IsEquivalentTo(calls, CollectionOrdering.Matching);
        foreach (var call in scenario.FileSystem.ReceivedCalls())
        {
            await Assert.That(call.GetArguments()[^1]).IsEqualTo(caller.Token);
        }
        await Assert.That(await scenario.ReadTextAsync(scenario.Paths.ConfigFile!)).IsEqualTo(initial);
        await Assert.That(scenario.Files.Directory.GetFiles(scenario.Root, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
    }
}
