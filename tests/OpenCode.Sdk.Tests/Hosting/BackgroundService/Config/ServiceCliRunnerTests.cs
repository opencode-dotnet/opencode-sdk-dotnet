using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceCliRunnerTests
{
    private static readonly ResolvedExecutable Executable = new("cli", "/tools/cli", IsBatchScript: false);

    [Test]
    public async Task RunAsync_Should_Not_Launch_When_PreCancelled()
    {
        await using var scenario = new LauncherChildScenario();
        // A launch the guard should prevent then ends at once, so the assertions below see it.
        scenario.Exits.Exit(new ChildExitStatus { ExitCode = 0 });
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();
        var exception = await Assert.That(async () =>
            await new ServiceCliRunner(scenario.Seams, scenario.LaunchAsync).RunAsync(Executable, [], null, caller.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(scenario.Start).IsNull();
        await Assert.That(scenario.Seams.Spawn.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(0, null, null)]
    [Arguments(3, null, "exited with code 3")]
    [Arguments(null, null, "exited with an unknown status")]
    [Arguments(null, 15, "terminated on signal 15")]
    [Arguments(-1073741819, null, "exited with code -1073741819 (0xC0000005)")]
    public async Task RunAsync_Should_Use_The_Observed_Exit_And_Release_The_Child(int? code, int? signal, string? diagnostic)
    {
        await using var scenario = new LauncherChildScenario();
        scenario.Exits.Exit(new ChildExitStatus { ExitCode = code, Signal = signal });
        var running = new ServiceCliRunner(scenario.Seams, scenario.LaunchAsync).RunAsync(Executable, ["service"], null, CancellationToken.None);
        Task[] tasks = [running];
        if (diagnostic is null)
        {
            await running;
        }
        else
        {
            var failure = await Assert.That(async () => await Task.WhenAll(tasks)).Throws<OpenCodeServerException>();
            await Assert.That(failure!.Message).IsEqualTo($"CLI command '/tools/cli' {diagnostic}. Recent stderr: ");
        }
        await Assert.That(scenario.Request!.Arguments).IsEquivalentTo(["service"], CollectionOrdering.Matching);
        await Assert.That(scenario.Start!.Password).IsNull();
        await Assert.That(scenario.Start.LauncherArguments).IsEmpty();
        await Assert.That(scenario.Start.WorkingDirectory).IsNull();
        await Assert.That(scenario.Start.Output).IsNull();
        await Assert.That(scenario.Start.ForceStandardErrorPipe).IsTrue();
        await Assert.That(scenario.Start.GracefulShutdownTimeout).IsEqualTo(new OpenCodeServerOptions().GracefulShutdownTimeout);
        await Assert.That(scenario.Lease.CanWrite).IsFalse();
    }

    [Test]
    public async Task RunAsync_Should_Cancel_Its_Wait_And_Release_The_Child()
    {
        await using var scenario = new LauncherChildScenario();
        using var caller = new CancellationTokenSource();
        var running = new ServiceCliRunner(scenario.Seams, scenario.LaunchAsync).RunAsync(Executable, [], null, caller.Token);
        await Assert.That(scenario.Child).IsNotNull();
        await caller.CancelOnWorkerAsync();
        scenario.Exits.Exit(new ChildExitStatus { ExitCode = 0 });
        Task[] tasks = [running];
        var failure = await Assert.That(async () => await Task.WhenAll(tasks)).Throws<OperationCanceledException>();
        await Assert.That(failure!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(scenario.Lease.CanWrite).IsFalse();
    }
}
