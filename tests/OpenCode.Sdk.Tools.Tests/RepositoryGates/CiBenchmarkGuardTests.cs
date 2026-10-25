using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class CiBenchmarkGuardTests
{
    private const string Run = "dotnet run --project tests/OpenCode.Sdk.Performance.Tests -c Release -f net10.0 --";
    private const string SdkFilter = "--filter 'OpenCode.Sdk.Performance.Tests.Benchmarks.*' --job Dry";

    [Test]
    public async Task Ci_Should_Keep_The_Ordered_Sdk_Dry_Commands()
    {
        var matrix = Job(await WorkflowAsync(), "build-and-test");
        var steps = DrySteps(matrix).ToArray();
        await Assert.That(Commands(matrix)).IsEquivalentTo([
            $"{Run} {SdkFilter} --runtimes net10.0 --artifacts .benchmarks/ci-sdk-net10",
            $"{Run} {SdkFilter} --runtimes net11.0 --artifacts .benchmarks/ci-sdk-net11",
            $"{Run} {SdkFilter} --runtimes net472 --artifacts .benchmarks/ci-sdk-net472",
        ], CollectionOrdering.Matching);
        await Assert.That(steps.Select(static step => step.Split('\n')[1].Trim())).IsEquivalentTo([
            "if: ${{ !cancelled() && steps.build.outcome == 'success' && needs.changes.outputs.benchmarks == 'true' && runner.os == 'Linux' }}",
            "if: ${{ !cancelled() && steps.build.outcome == 'success' && needs.changes.outputs.benchmarks == 'true' && runner.os == 'Linux' }}",
            "if: ${{ !cancelled() && steps.build.outcome == 'success' && needs.changes.outputs.benchmarks == 'true' && runner.os == 'Windows' }}",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Ci_Should_Run_Generator_Stages_In_Their_Own_Job_Without_Runtimes()
    {
        var stage = Job(await WorkflowAsync(), "benchmark-generator-stage");
        await Assert.That(stage).Contains("needs: changes");
        await Assert.That(stage).Contains("if: needs.changes.outputs.benchmarks == 'true'");
        await Assert.That(stage).Contains("runs-on: ubuntu-24.04");
        await Assert.That(Commands(stage)).IsEquivalentTo([
            $"{Run} --filter 'OpenCode.Sdk.Performance.Tests.Generator.GeneratorStageBenchmarks.*' --job Dry --artifacts .benchmarks/ci-generator-stage",
        ], CollectionOrdering.Matching);
        await Assert.That(stage).DoesNotContain("--runtimes");
        await Assert.That(stage).DoesNotContain("GeneratorRunBenchmarks");
        await Assert.That(stage).DoesNotContain("OPENCODE_SDK_BENCHMARKS_REWRITE_TREE");
    }

    [Test]
    public async Task Ci_Should_Bound_Every_Dry_Step()
    {
        var timeouts = DrySteps(await WorkflowAsync()).Select(static step =>
            step.Split('\n').SingleOrDefault(static line => line.TrimStart().StartsWith("timeout-minutes:", StringComparison.Ordinal))?.Trim() ?? "<missing>");
        await Assert.That(timeouts).IsEquivalentTo([
            "timeout-minutes: 20", "timeout-minutes: 20", "timeout-minutes: 20", "timeout-minutes: 20",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Ci_Should_Require_The_Stage_Result_Only_When_Selected()
    {
        var workflow = await WorkflowAsync();
        var changes = Job(workflow, "changes");
        await Assert.That(changes).Contains("benchmarks: ${{ steps.diff.outputs.benchmarks }}");
        await Assert.That(changes).Contains("echo \"benchmarks=true\" >> \"$GITHUB_OUTPUT\"");
        await Assert.That(changes).Contains("src/* | tools/OpenCode.Sdk.Tools/* | tools/curation.json | tools/generation-profile.txt | spec/* | tests/OpenCode.Sdk.Performance.Tests/* | tests/Shared/BenchmarkExitPolicy.cs | Directory.Build.props | Directory.Build.targets | Directory.Packages.props | global.json | .github/workflows/ci.yml) benchmarks=true ;;");
        await Assert.That(changes).Contains("echo \"benchmarks=$benchmarks\" >> \"$GITHUB_OUTPUT\"");
        var aggregate = Job(workflow, "ci-result");
        await Assert.That(aggregate).Contains("needs: [changes, lint, build-and-test, benchmark-generator-stage]");
        await Assert.That(aggregate).Contains("if: always()");
        await Assert.That(aggregate).Contains("BENCHMARKS: ${{ needs.changes.outputs.benchmarks }}");
        await Assert.That(aggregate).Contains("STAGE: ${{ needs.benchmark-generator-stage.result }}");
        await Assert.That(aggregate).Contains("[ \"$CHANGES\" = success ] || exit 1");
        await Assert.That(aggregate).Contains("""
                      if [ "$BENCHMARKS" = true ]; then
                        [ "$STAGE" = success ] || exit 1
                      else
                        [ "$BENCHMARKS" = false ] && [ "$STAGE" = skipped ] || exit 1
                      fi
            """);
    }

    private static async Task<string> WorkflowAsync()
    {
        var fileSystem = new RealFileSystem();
        return (await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(new RepositoryRoot(fileSystem).Locate(), ".github", "workflows", "ci.yml")))
            .ReplaceLineEndings("\n");
    }

    private static string Job(string workflow, string name) =>
        Regex.Match(workflow, $@"(?ms)^  {Regex.Escape(name)}:\n.*?(?=^  \S|\z)", RegexOptions.None, Regex.InfiniteMatchTimeout).Value;

    private static IEnumerable<string> DrySteps(string workflow) =>
        workflow.Split("      - name: ", StringSplitOptions.None).Where(static step => step.Contains("--job Dry", StringComparison.Ordinal));

    private static IEnumerable<string> Commands(string workflow) =>
        workflow.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith($"run: {Run}", StringComparison.Ordinal))
            .Select(static line => line[5..]);
}
