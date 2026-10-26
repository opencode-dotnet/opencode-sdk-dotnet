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
        var workflow = await WorkflowAsync();
        await Assert.That(Job(workflow, "build-and-test")).DoesNotContain("--job Dry");
        await Assert.That(Job(workflow, "publish-nightly")).Contains("needs: [lint, build-and-test, benchmark-sdk]");
        var matrix = Job(workflow, "benchmark-sdk");
        await Assert.That(matrix).Contains("needs: changes");
        await Assert.That(matrix).Contains("if: needs.changes.outputs.benchmarks == 'true'");
        await Assert.That(matrix).Contains("fail-fast: false");
        await Assert.That(Commands(matrix)).IsEquivalentTo([
            $"{Run} {SdkFilter} --runtimes ${{{{ matrix.runtime }}}} --artifacts .benchmarks/ci-sdk-${{{{ matrix.leg }}}}",
            $"{Run} --filter 'OpenCode.Sdk.Performance.Tests.ColdStart.*' --artifacts .benchmarks/ci-sdk-coldstart",
        ], CollectionOrdering.Matching);
        await Assert.That(matrix.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith("- {", StringComparison.Ordinal))).IsEquivalentTo([
            "- { runtime: net10.0, os: ubuntu-24.04, leg: net10 }",
            "- { runtime: net11.0, os: ubuntu-24.04, leg: net11 }",
            "- { runtime: net472, os: windows-2025, leg: net472 }",
        ], CollectionOrdering.Matching);
        var uploadStart = matrix.IndexOf("      - name: Upload SDK Dry artifacts\n", StringComparison.Ordinal);
        var uploadEnd = matrix.IndexOf("\n      - name: ", uploadStart + 1, StringComparison.Ordinal);
        var upload = matrix[uploadStart..(uploadEnd < 0 ? matrix.Length : uploadEnd)];
        await Assert.That(upload).Contains("if: always()");
        await Assert.That(upload).Contains("name: benchmark-sdk-${{ matrix.leg }}-${{ github.event.pull_request.number || github.run_number }}");
        await Assert.That(upload).Contains("continue-on-error: true");
    }

    [Test]
    public async Task Ci_Should_Run_Cold_Start_Once_On_The_Net10_Leg()
    {
        var matrix = Job(await WorkflowAsync(), "benchmark-sdk");
        await Assert.That(matrix).Contains("      - name: SDK cold start (net10)\n");
        var start = matrix.IndexOf("      - name: SDK cold start (net10)\n", StringComparison.Ordinal);
        var end = matrix.IndexOf("\n      - name: ", start + 1, StringComparison.Ordinal);
        var step = matrix[start..(end < 0 ? matrix.Length : end)];
        await Assert.That(step).Contains("if: matrix.leg == 'net10'");
        await Assert.That(step).Contains("timeout-minutes: 20");
        var command = Commands(step).Single();
        await Assert.That(command).DoesNotContain("--job");
        await Assert.That(command).DoesNotContain("--runtimes");
    }

    [Test]
    public async Task Ci_Should_Run_Generator_Stages_In_Their_Own_Job_Without_Runtimes()
    {
        var stage = Job(await WorkflowAsync(), "benchmark-generator-stage");
        await Assert.That(stage).Contains("needs: changes");
        await Assert.That(stage).Contains("if: needs.changes.outputs.benchmarks-generator == 'true'");
        await Assert.That(stage).Contains("- name: Generator stages\n");
        await Assert.That(stage).Contains("- name: Upload generator stage artifacts\n");
        var uploadStart = stage.IndexOf("      - name: Upload generator stage artifacts\n", StringComparison.Ordinal);
        var uploadEnd = stage.IndexOf("\n      - name: ", uploadStart + 1, StringComparison.Ordinal);
        var upload = stage[uploadStart..(uploadEnd < 0 ? stage.Length : uploadEnd)];
        await Assert.That(upload).Contains("continue-on-error: true");
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
            "timeout-minutes: 20", "timeout-minutes: 20",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Ci_Should_Require_The_Stage_Result_Only_When_Selected()
    {
        var workflow = await WorkflowAsync();
        var changes = Job(workflow, "changes");
        await Assert.That(changes).Contains("benchmarks: ${{ steps.diff.outputs.benchmarks }}");
        await Assert.That(changes).Contains("echo \"benchmarks=true\" >> \"$GITHUB_OUTPUT\"");
        await Assert.That(changes).Contains("src/* | tools/OpenCode.Sdk.Tools/* | tools/curation.json | tools/generation-profile.txt | spec/* | tests/OpenCode.Sdk.Performance.Tests/* | tests/Shared/BenchmarkExitPolicy.cs | tests/Directory.Build.props | Directory.Build.props | Directory.Build.targets | Directory.Packages.props | global.json | nuget.config | .github/workflows/ci.yml) benchmarks=true ;;");
        await Assert.That(changes).Contains("echo \"benchmarks=$benchmarks\" >> \"$GITHUB_OUTPUT\"");
        await Assert.That(changes).Contains("benchmarks-generator: ${{ steps.diff.outputs.benchmarks-generator }}");
        await Assert.That(changes).Contains("echo \"benchmarks-generator=true\" >> \"$GITHUB_OUTPUT\"");
        await Assert.That(changes).Contains("benchmarks_generator=false");
        await Assert.That(changes).Contains("              tools/* | tests/OpenCode.Sdk.Tools.Tests/* | tests/OpenCode.Sdk.Performance.Tests/* | spec/* | tests/Shared/BenchmarkExitPolicy.cs | tests/Directory.Build.props | Directory.Build.props | Directory.Build.targets | Directory.Packages.props | global.json | nuget.config | .github/workflows/ci.yml) benchmarks_generator=true ;;");
        await Assert.That(changes).Contains("echo \"benchmarks-generator=$benchmarks_generator\" >> \"$GITHUB_OUTPUT\"");
        var aggregate = Job(workflow, "ci-result");
        await Assert.That(aggregate).Contains("needs: [changes, lint, build-and-test, benchmark-sdk, benchmark-generator-stage]");
        await Assert.That(aggregate).Contains("SDK_BENCHMARKS: ${{ needs.changes.outputs.benchmarks }}");
        await Assert.That(aggregate).Contains("SDK: ${{ needs.benchmark-sdk.result }}");
        await Assert.That(aggregate).Contains("""
                      if [ "$SDK_BENCHMARKS" = true ]; then
                        [ "$SDK" = success ] || exit 1
                      else
                        [ "$SDK_BENCHMARKS" = false ] && [ "$SDK" = skipped ] || exit 1
                      fi
            """);
        await Assert.That(aggregate).Contains("if: always()");
        await Assert.That(aggregate).Contains("BENCHMARKS: ${{ needs.changes.outputs.benchmarks-generator }}");
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
