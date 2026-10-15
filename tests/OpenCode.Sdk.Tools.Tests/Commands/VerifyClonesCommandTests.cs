using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Tests.Clones.Support;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace OpenCode.Sdk.Tools.Tests.Commands;

/// <summary>The clone gate's verdict: every scope runs and prints, a gated scope's failure fails the gate, and a missing prerequisite refuses it.</summary>
public sealed class VerifyClonesCommandTests
{
    private const string Command = "verify-clones";
    private const string NewCloneError = "Using config from work/src.json\nERROR: jscpd found 1 new clones not in the baseline (allowed: 0)";

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
        await Assert.That(result.Output).Contains("tests: 8 clones, 80 of 1000 lines (8.00%), reported only");
        await Assert.That(result.Output).Contains("generated: 9 clones");
        await Assert.That(result.Output).Contains("Clone gate passed");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_A_Gated_Scope_With_A_New_Clone_After_Every_Scope_Ran()
    {
        var scenario = await new CloneScenario()
            .WithRun("src", exitCode: 1, clones: 5, newClones: 1, standardError: NewCloneError)
            .WithRun("tools")
            .WithRun("tests")
            .WithRun("generated")
            .BuildAsync();

        var result = await RunAsync(scenario, Command);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(scenario.Runner.Invocations.Count).IsEqualTo(4);
        await Assert.That(result.Output).Contains("Clone gate failed for 'src'").And.Contains("1 new clones not in the baseline");
        await Assert.That(result.Output).DoesNotContain("Using config from");
    }

    [Test]
    public async Task Execute_Should_Fail_Naming_A_Report_Scope_Whose_Run_Failed_After_Writing_Its_Report()
    {
        var scenario = await new CloneScenario()
            .WithRun("src")
            .WithRun("tools")
            .WithRun("tests", exitCode: 1, standardError: "ERROR: no files analysed")
            .WithRun("generated")
            .BuildAsync();

        var result = await RunAsync(scenario, Command);

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Output).Contains("Clone gate failed for 'tests'").And.Contains("no files analysed");
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
        await Assert.That(updated.Runner.Invocations.Count(static invocation => invocation.Contains("--update-baseline", StringComparison.Ordinal))).IsEqualTo(2);
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
        var tester = new CommandAppTester(registrar);
        tester.Configure(ToolApp.Configure);
        return await tester.RunAsync(arguments);
    }
}
