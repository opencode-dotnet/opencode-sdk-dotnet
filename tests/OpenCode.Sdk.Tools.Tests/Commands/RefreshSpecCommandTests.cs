using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Spectre.Console;
using Spectre.Console.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Commands;

public sealed class RefreshSpecCommandTests
{
    [Test]
    public async Task Execute_Should_Cancel_Prepare_During_Install_And_Remove_Its_Worktree()
    {
        using var scenario = await RefreshScenario.CreateAsync();
        var runner = scenario.ExpectReference(RefreshScenario.Document);
        var fs = scenario.FileSystem;
        var submodule = fs.Path.GetFullPath(SnapshotPaths.Submodule);
        var worktree = fs.Path.GetFullPath(fs.Path.Combine(SnapshotPaths.ScratchRoot, RefreshScenarioData.Commit, "worktree"));
        var registered = new HashSet<string>(StringComparer.Ordinal);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        runner.Expect("git", ["-c", "core.longpaths=true", "worktree", "add", "--detach", worktree, RefreshScenarioData.Commit], submodule,
            sideEffect: () => { registered.Add(worktree); return Task.CompletedTask; });
        runner.Expect("bun", ["install", "--frozen-lockfile", "--ignore-scripts"], worktree, sideEffect: async () =>
        {
            var received = runner.CancellationTokens[^1];
            entered.SetResult(received);
            if (!received.CanBeCanceled)
            {
                throw new InvalidOperationException("The running install received no cancellation token.");
            }
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = received.Register(() => cancelled.SetResult());
            await cancelled.Task;
            received.ThrowIfCancellationRequested();
        });
        var removal = $"git -c core.longpaths=true worktree remove --force {worktree}";
        runner.Expect("git", ["-c", "core.longpaths=true", "worktree", "remove", "--force", worktree], submodule,
            sideEffect: () => { registered.Remove(worktree); return Task.CompletedTask; });
        using var console = new TestConsole();
        var run = ToolApp.RunAsync(["refresh-spec", "--ref", "origin/v2"], cancellation.Token, services =>
        {
            services.AddSingleton<IFileSystem>(fs);
            services.AddSingleton<IProcessRunner>(runner);
            services.AddSingleton<IAnsiConsole>(console);
        });
        var completed = await Task.WhenAny(entered.Task, run);
        await Assert.That(completed).IsSameReferenceAs(entered.Task);
        var token = await entered.Task;
        await cancellation.CancelAsync();
        var exitCode = await run;

        await Assert.That(token).IsEqualTo(cancellation.Token);
        await Assert.That(exitCode).IsEqualTo(130);
        await Assert.That(registered).IsEmpty();
        await Assert.That(runner.CancellationTokens[^1]).IsEqualTo(CancellationToken.None);
        await Assert.That(runner.Invocations.TakeLast(2)).IsEquivalentTo(
            ["bun install --frozen-lockfile --ignore-scripts", removal], CollectionOrdering.Matching);
        await Assert.That(runner.Invocations.Count(invocation => string.Equals(invocation, removal, StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(console.Output).IsEmpty();
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    public async Task Execute_Should_Refuse_Anything_But_One_Mode(bool prepare, bool verify, bool apply)
    {
        using var scenario = new RefreshScenario();
        var arguments = new List<string> { "refresh-spec" };
        if (prepare)
        {
            arguments.AddRange(["--ref", "v2.0.30"]);
        }
        if (verify)
        {
            arguments.Add("--verify");
        }
        if (apply)
        {
            arguments.AddRange(["--apply", RefreshScenario.ReceiptPath]);
        }

        var result = await scenario.RunCommandAsync([.. arguments]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(result.Output).Contains("Specify exactly one of --ref <REF>, --verify, or --apply <RECEIPT>.");
        await Assert.That(scenario.Runner.Invocations).IsEmpty();
    }

    [Test]
    [Arguments("prepare", 0, "Receipt prepared:")]
    [Arguments("apply", 0, "Accepted snapshot applied:")]
    [Arguments("verify", 0, "The accepted snapshot reproduces its receipt")]
    [Arguments("unreproduced", 1, "no committed receipt exists; the accepted snapshot predates the synchronizer")]
    [Arguments("refusal", 1, "Refused: receipt '[red].json' does not exist")]
    public async Task Execute_Should_Dispatch_One_Mode_And_Map_Its_Outcome(string mode, int exitCode, string diagnostic)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        if (mode == "prepare")
        {
            _ = scenario.ExpectReference(RefreshScenario.Document, "v2.0.30", "refs/tags/v2.0.30");
            scenario.ExpectUnpatchedGeneration();
        }
        else if (mode == "apply")
        {
            _ = scenario.ExpectApply();
        }
        else if (mode != "refusal")
        {
            scenario.ExpectHead();
        }
        if (mode == "unreproduced")
        {
            scenario.FileSystem.File.Delete(SnapshotPaths.CommittedReceipt);
        }
        var arguments = mode switch
        {
            "prepare" => new[] { "--ref", "v2.0.30" },
            "apply" => ["--apply", RefreshScenario.ReceiptPath],
            "refusal" => ["--apply", "[red].json"],
            _ => ["--verify"],
        };

        var result = await scenario.RunCommandAsync(["refresh-spec", .. arguments]);

        await Assert.That(result.ExitCode).IsEqualTo(exitCode);
        await Assert.That(result.Output).Contains(diagnostic);
    }
}
