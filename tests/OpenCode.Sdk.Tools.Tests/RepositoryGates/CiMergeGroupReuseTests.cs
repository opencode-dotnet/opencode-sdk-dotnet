using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class CiMergeGroupReuseTests
{
    [Test]
    public async Task Ci_Should_Build_And_Run_The_Reuse_Judge_From_The_Base_Worktree()
    {
        var fileSystem = new RealFileSystem();
        var workflow = await new CiWorkflow(fileSystem).ReadAsync(new RepositoryRoot(fileSystem).Locate());
        var reuse = CiWorkflow.Job(workflow, "reuse");
        await Assert.That(reuse).Contains("QUEUE_BASE: ${{ github.event.merge_group.base_sha }}");
        await Assert.That(reuse).Contains("if ! git worktree add --detach \"$RUNNER_TEMP/judge\" \"$QUEUE_BASE\"; then");
        var steps = reuse.Split("      - name: ", StringSplitOptions.None);
        var dotnetSteps = steps.Where(static step => step.Split('\n').Select(static line => line.TrimStart())
                .Any(static line => line.StartsWith("dotnet ", StringComparison.Ordinal) || line.StartsWith("run: dotnet ", StringComparison.Ordinal)))
            .Select(static step => step[..step.IndexOf('\n', StringComparison.Ordinal)]);
        await Assert.That(dotnetSteps).IsEquivalentTo(["Decide reuse"], CollectionOrdering.Matching);
        var setup = steps.Single(static step => step.StartsWith("Setup .NET SDK\n", StringComparison.Ordinal));
        await Assert.That(setup).Contains("global-json-file: ${{ runner.temp }}/judge/global.json");
        var decide = steps.Single(static step => step.StartsWith("Decide reuse\n", StringComparison.Ordinal));
        await Assert.That(decide).Contains("working-directory: ${{ runner.temp }}/judge");
        await Assert.That(decide.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith("dotnet ", StringComparison.Ordinal)))
            .IsEquivalentTo(["dotnet run --file tools/opencode-tool.cs -- merge-group-reuse --facts \"$RUNNER_TEMP/reuse\" --output \"$GITHUB_OUTPUT\""], CollectionOrdering.Matching);
        await Assert.That(reuse).DoesNotContain("GITHUB_WORKSPACE");
        await Assert.That(reuse).DoesNotContain("github.workspace");

        var permissions = reuse.Split('\n').SkipWhile(static line => line != "    permissions:").Skip(1)
            .TakeWhile(static line => line.StartsWith("      ", StringComparison.Ordinal)).Select(static line => line.Trim());
        await Assert.That(permissions).IsEquivalentTo([
            "contents: read", "actions: read", "pull-requests: read",
        ], CollectionOrdering.Matching);
    }
}
