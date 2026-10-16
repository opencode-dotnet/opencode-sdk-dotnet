using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Tests.Support;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace OpenCode.Sdk.Tools.Tests.Commands;

/// <summary>The clone gate's verdict: every scope runs and prints, a gated scope's failure fails the gate, and a missing prerequisite refuses it.</summary>
public sealed class VerifyClonesCommandTests
{
    private const string Command = "verify-clones";

    [Test]
    [Arguments("src")]
    [Arguments("tools")]
    [Arguments("tests")]
    public async Task Execute_Should_Fail_On_Each_Stale_Fingerprint_Until_The_Baseline_Is_Updated(string scope)
    {
        var scenario = await new CloneScenario()
            .WithBaseline(scope, CloneScenario.KnownFingerprint, "deadbeefdeadbeef", "fedcbafedcbafedcb")
            .WithPassingRuns()
            .WithPassingRuns(updateBaseline: true)
            .WithPassingRuns()
            .BuildAsync();

        var stale = await RunAsync(scenario, Command);
        var updated = await RunAsync(scenario, Command, "--update-baseline");
        var current = await RunAsync(scenario, Command);

        await Assert.That(stale.ExitCode).IsEqualTo(1);
        await Assert.That(stale.Output).Contains($"Clone gate failed for '{scope}'");
        await Assert.That(stale.Output).Contains("stale baseline fingerprint: deadbeefdeadbeef");
        await Assert.That(stale.Output).Contains("stale baseline fingerprint: fedcbafedcbafedcb");
        await Assert.That(stale.Output).Contains("verify-clones --update-baseline");
        await Assert.That(stale.Output).DoesNotContain("stale baseline fingerprint: " + CloneScenario.KnownFingerprint);
        await Assert.That(updated.ExitCode).IsEqualTo(0);
        await Assert.That(current.ExitCode).IsEqualTo(0);
        await Assert.That(current.Output).Contains("Clone gate passed");
        await Assert.That(scenario.Runner.Invocations.Count).IsEqualTo(12);
    }

    [Test]
    public async Task Execute_Should_Keep_A_Refusal_Unwrapped_And_Restore_The_Console_Width()
    {
        var scenario = await new CloneScenario().WithoutSolution().BuildAsync();
        var root = scenario.FileSystem.Path.Combine(scenario.Root, "[red]" + new string('x', 160));
        scenario.FileSystem.Directory.CreateDirectory(root);
        scenario.FileSystem.Directory.SetCurrentDirectory(root);
        using var registrar = ToolApp.CreateRegistrar(services =>
            services.AddSingleton<IFileSystem>(scenario.FileSystem));
        using var console = new TestConsole();
        console.Width(80);
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings(), console);
        tester.Configure(ToolApp.Configure);

        var result = await tester.RunAsync([Command]);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).IsEqualTo($"Refused: Run verify-clones from the repository root: no OpenCode.slnx in '{root}'.");
        await Assert.That(console.Profile.Width).IsEqualTo(80);
    }

    [Test]
    public async Task Execute_Should_Pass_And_Print_Every_Scope_When_No_Gated_Scope_Fails()
    {
        var scenario = await new CloneScenario()
            .WithRun("src", clones: 4)
            .WithRun("tools", clones: 2)
            .WithRun("tests", clones: 8)
            .WithRun("generated", clones: 9)
            .BuildAsync();

        var result = await RunAsync(scenario, Command);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("src: 4 clones, 40 of 1000 lines (4.00%), 0 new");
        await Assert.That(result.Output).Contains("tests: 8 clones, 80 of 1000 lines (8.00%), 0 new");
        await Assert.That(result.Output).Contains("generated: 9 clones");
        await Assert.That(result.Output).Contains("Clone gate passed");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_A_Gated_Scope_With_A_New_Clone_After_Every_Scope_Ran()
    {
        var scenario = await new CloneScenario().WithNewCloneFailure().BuildAsync();

        var result = await RunAsync(scenario, Command);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(scenario.Runner.Invocations.Count).IsEqualTo(4);
        await Assert.That(result.Output).Contains("Clone gate failed for 'src'").And.Contains("1 new clones not in the baseline");
        await Assert.That(result.Output).DoesNotContain("Using config from");
        await Assert.That(result.Output).Contains("new clone (212 tokens): src/NestedFolderPastTheWrapColumn/A0.cs:3-33 <-> src/NestedFolderPastTheWrapColumn/B0.cs:5-35");
        await Assert.That(result.Output).DoesNotContain("Old.cs");
        // The test console is 80 columns wide, as a redirected CI log is; both lines are longer and
        // must still arrive whole.
        await Assert.That(result.Output).Contains("Clone gate failed for 'src': ERROR: jscpd found 1 new clones not in the baseline (allowed: 0)");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_A_Report_Scope_Whose_Run_Failed_After_Writing_Its_Report()
    {
        var scenario = await new CloneScenario()
            .WithRun("src")
            .WithRun("tools")
            .WithRun("tests")
            .WithRun("generated", exitCode: 1, standardError: "ERROR: no files analysed")
            .BuildAsync();

        var result = await RunAsync(scenario, Command);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Clone gate failed for 'generated'").And.Contains("no files analysed");
    }

    [Test]
    public async Task Execute_Should_Refuse_A_Missing_Baseline_Unless_It_Updates_The_Baselines()
    {
        var refused = await new CloneScenario().WithoutBaselines().BuildAsync();
        var updated = await new CloneScenario().WithoutBaselines().WithPassingRuns().BuildAsync();

        var refusal = await RunAsync(refused, Command);
        var update = await RunAsync(updated, Command, "--update-baseline");

        await Assert.That(refusal.ExitCode).IsEqualTo(1);
        await Assert.That(refusal.Output).Contains("--update-baseline and commit the baseline");
        await Assert.That(refused.Runner.Invocations).IsEmpty();
        await Assert.That(update.ExitCode).IsEqualTo(0);
        await Assert.That(update.Output).Contains("Clone baselines updated");
        await Assert.That(updated.Runner.Invocations.Count(static invocation => invocation.Contains("--update-baseline", StringComparison.Ordinal))).IsEqualTo(3);
    }

    [Test]
    public async Task Execute_Should_Keep_Every_Scope_s_Report_In_The_Requested_Directory()
    {
        var scenario = await new CloneScenario().WithNewCloneFailure().BuildAsync();
        var reports = scenario.FileSystem.Path.Combine(scenario.Root, "clone-reports");

        var result = await RunAsync(scenario, Command, "--report-directory", reports);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(scenario.FileSystem.Directory.EnumerateFiles(reports).Select(path => scenario.FileSystem.Path.GetFileName(path)))
            .IsEquivalentTo(["src.json", "tools.json", "tests.json", "generated.json"]);
        await Assert.That(await scenario.FileSystem.File.ReadAllTextAsync(scenario.FileSystem.Path.Combine(reports, "src.json")))
            .Contains("\"isNew\": true");
    }

    [Test]
    public async Task Execute_Should_Refuse_Outside_The_Repository_Root()
    {
        var scenario = await new CloneScenario().WithoutSolution().BuildAsync();

        var result = await RunAsync(scenario, Command);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Run verify-clones from the repository root");
    }

    [Test]
    public async Task Execute_Should_Remove_Its_Work_Folder_After_A_Pass_And_After_A_Refusal()
    {
        var passed = await new CloneScenario().WithPassingRuns().BuildAsync();
        var refused = await new CloneScenario().WithRun("src", writesReport: false).BuildAsync();

        var pass = await RunAsync(passed, Command);
        var refusal = await RunAsync(refused, Command);

        await Assert.That(pass.ExitCode).IsEqualTo(0);
        await Assert.That(passed.Runner.Invocations.Count).IsEqualTo(4);
        await Assert.That(refusal.ExitCode).IsEqualTo(1);
        await Assert.That(refusal.Output).Contains("jscpd wrote no report for scope 'src'");
        await Assert.That(refused.Runner.Invocations.Count).IsEqualTo(1);
        await Assert.That(passed.WorkFolders()).IsEmpty();
        await Assert.That(refused.WorkFolders()).IsEmpty();
    }

    private static async Task<CommandAppResult> RunAsync(CloneScenario scenario, params string[] arguments)
    {
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(scenario.FileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
            services.AddSingleton<IProcessRunner>(scenario.Runner);
        });
        // The command writes to the tester's console. It is pinned to the 80 columns of a redirected CI
        // log, so a line the gate lets wrap is split in the output and fails the assertions.
        using var console = new TestConsole();
        _ = console.Width(80);
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings(), console);
        tester.Configure(ToolApp.Configure);
        return await tester.RunAsync(arguments);
    }
}
