using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SnapshotApplyPlannerTests
{
    [Test]
    public async Task PlanAsync_Should_Not_Rewrite_An_Unchanged_Watch()
    {
        using var scenario = await RefreshScenario.CreateAsync();
        scenario.WithWatch(DocumentInspector.Sha256Hex("anchor"u8.ToArray()));
        scenario.ExpectCommit();

        var plan = await scenario.Services.GetRequiredService<SnapshotApplyPlanner>().PlanAsync(scenario.State, scenario.Receipt, CancellationToken.None);

        await Assert.That(plan.SourceWatch).IsNull();
    }

    [Test]
    [Arguments("schema", "schema version")]
    [Arguments("sha", "not a full SHA")]
    [Arguments("null path", "no prepared document")]
    [Arguments("missing path", "no prepared document")]
    [Arguments("digest", "operation-set digest")]
    [Arguments("operations", "operation count")]
    [Arguments("components", "component count")]
    [Arguments("content", "contentSchema count")]
    [Arguments("watch", "source watch")]
    [Arguments("commit", "verifying the receipt commit exists failed")]
    public async Task PlanAsync_Should_Refuse_An_Invalid_Candidate(string variation, string diagnostic)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        var receipt = variation switch
        {
            "schema" => scenario.Receipt with { SchemaVersion = 1 },
            "sha" => scenario.Receipt with { UpstreamCommit = "short" },
            "null path" => scenario.Receipt with { NormalizedDocumentPath = null },
            "missing path" => scenario.Receipt with { NormalizedDocumentPath = "missing.json" },
            _ => RefreshScenarioData.WithStatistic(scenario.Receipt, variation),
        };
        var state = variation == "watch" ? scenario.State with { SourceWatch = "null"u8.ToArray() } : scenario.State;
        scenario.Runner.Expect("git", ["rev-parse", "--verify", $"{RefreshScenarioData.Commit}^{{commit}}"],
            scenario.FileSystem.Path.GetFullPath(SnapshotPaths.Submodule), ScriptedProcessRunner.Fail("missing commit"));

        var exception = await Assert.That(async () => _ = await scenario.Services.GetRequiredService<SnapshotApplyPlanner>()
            .PlanAsync(state, receipt, CancellationToken.None)).Throws<SnapshotRefreshException>();
        await Assert.That(exception!.Message).Contains(diagnostic);
    }

    [Test]
    public async Task PlanAsync_Should_Render_The_Injected_Utc_Date()
    {
        using var scenario = await RefreshScenario.CreateAsync();
        scenario.ExpectCommit();
        var plan = await scenario.Services.GetRequiredService<SnapshotApplyPlanner>().PlanAsync(scenario.State, scenario.Receipt, CancellationToken.None);

        await Assert.That(plan.Markdown.Split('\n').Single(static line => line.StartsWith("Date:", StringComparison.Ordinal))).IsEqualTo("Date: 2031-02-03");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    public async Task PlanAsync_Should_Refuse_A_Receipt_Without_ReleaseTag(string? tag)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        scenario.ExpectCommit();
        var exception = await Assert.That(async () => _ = await scenario.Services.GetRequiredService<SnapshotApplyPlanner>()
            .PlanAsync(scenario.State, scenario.Receipt with { ReleaseTag = tag }, CancellationToken.None)).Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("releaseTag");
        await Assert.That(await scenario.FileSystem.File.ReadAllBytesAsync(SnapshotPaths.AcceptedDocument))
            .IsEquivalentTo(RefreshScenario.Document, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
