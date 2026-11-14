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
        await Assert.That(reuse).Contains("git worktree add --detach \"$RUNNER_TEMP/judge\" \"$QUEUE_BASE\"");
        var steps = reuse.Split("      - name: ", StringSplitOptions.None);
        var setup = steps.Single(static step => step.StartsWith("Setup .NET SDK\n", StringComparison.Ordinal));
        await Assert.That(setup).Contains("global-json-file: ${{ runner.temp }}/judge/global.json");
        var decide = steps.Single(static step => step.StartsWith("Decide reuse\n", StringComparison.Ordinal));
        await Assert.That(decide).Contains("working-directory: ${{ runner.temp }}/judge");
        await Assert.That(decide).Contains("dotnet run --file tools/opencode-tool.cs -- merge-group-reuse");
        await Assert.That(reuse).DoesNotContain("GITHUB_WORKSPACE");
        foreach (var checkout in steps.Where(static step => step.Contains("uses: actions/checkout@", StringComparison.Ordinal)))
        {
            await Assert.That(checkout.Split('\n')).DoesNotContain(static line => line.TrimStart().StartsWith("path:", StringComparison.Ordinal));
        }

        var permissions = reuse.Split('\n').SkipWhile(static line => line != "    permissions:").Skip(1)
            .TakeWhile(static line => line.StartsWith("      ", StringComparison.Ordinal)).Select(static line => line.Trim());
        await Assert.That(permissions).IsEquivalentTo([
            "contents: read", "actions: read", "pull-requests: read",
        ], CollectionOrdering.Matching);
    }
}
