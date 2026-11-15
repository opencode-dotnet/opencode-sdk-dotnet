using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SourceWatchVerifierTests
{
    private static readonly WatchedSource Pinned =
        RefreshScenarioData.Watched("packages/core/src/pty/ticket.ts", "const DEFAULT_TTL = Duration.seconds(60)\n", "seconds(60)");

    [Test]
    public async Task Compare_Should_Report_Nothing_When_The_Pin_Reproduces()
    {
        var problems = SourceWatchVerifier.Compare([Pinned], [Observed(Pinned.Sha256, anchorMatched: true)]);

        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task Compare_Should_Report_A_Changed_Blob()
    {
        var problems = SourceWatchVerifier.Compare([Pinned], [Observed(new string('a', 64), anchorMatched: true)]);

        await Assert.That(problems.Single()).Contains("changed: pinned");
        await Assert.That(problems.Single()).Contains(Pinned.Behaviors.Single().Behavior);
    }

    [Test]
    public async Task Compare_Should_Report_A_Lost_Anchor()
    {
        var problems = SourceWatchVerifier.Compare([Pinned], [Observed(Pinned.Sha256, anchorMatched: false)]);

        await Assert.That(problems.Single()).Contains("no longer carries its anchor (contains 'seconds(60)')");
    }

    [Test]
    public async Task Compare_Should_Name_Only_The_Lost_Anchor_When_Another_Still_Matches()
    {
        var (source, observation) = RefreshScenarioData.OneLostAnchor(Pinned);

        var problems = SourceWatchVerifier.Compare([source], [observation]);

        await Assert.That(problems).IsEquivalentTo(
            [$"watched source '{source.Path}' no longer carries its anchor (contains 'alpha') for opencode-sdk-dotnet: the door's dependency on alpha"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Compare_Should_Report_A_Source_The_Observation_Skipped()
    {
        var problems = SourceWatchVerifier.Compare([Pinned], []);

        await Assert.That(problems.Single()).Contains("was not observed");
    }

    private static ReceiptWatchedSource Observed(string sha256, bool anchorMatched) =>
        new()
        {
            Path = Pinned.Path,
            Sha256 = sha256,
            Anchors = [new ReceiptWatchedAnchor { Text = "seconds(60)", Matched = anchorMatched }],
        };

    [Test]
    [Arguments("matching", null)]
    [Arguments("hash", "but this checkout carries")]
    [Arguments("anchor", "with different anchors than this checkout observes")]
    [Arguments("recorded only", "which the source watch no longer names")]
    [Arguments("observed only", "which the receipt does not record")]
    public async Task CompareReceipt_Should_Report_The_Recorded_Observation_Difference(string variation, string? diagnostic)
    {
        var recorded = Observed(Pinned.Sha256, anchorMatched: true);
        var observed = variation switch
        {
            "hash" => recorded with { Sha256 = new string('b', 64) },
            "anchor" => recorded with { Anchors = [new ReceiptWatchedAnchor { Text = "seconds(60)", Matched = false }] },
            _ => recorded,
        };
        var problems = SourceWatchVerifier.CompareReceipt(variation == "observed only" ? [] : [recorded],
            variation == "recorded only" ? [] : [observed]);

        if (diagnostic is null)
        {
            await Assert.That(problems).IsEmpty();
        }
        else
        {
            await Assert.That(problems.Single()).Contains(diagnostic).And.Contains(Pinned.Path);
        }
    }

    [Test]
    public async Task Compare_Should_Report_One_Moved_Hash_And_Each_Lost_Anchor_With_Its_Owner()
    {
        var pinned = Pinned with { Behaviors = [RefreshScenarioData.Behavior("zeta"), RefreshScenarioData.Behavior("alpha", WatchOwners.AgentsAi)] };
        var problems = SourceWatchVerifier.Compare([pinned], [RefreshScenarioData.Observation(pinned, "moved", anchorMatched: false)]);

        await Assert.That(problems).IsEquivalentTo(
        [
            $"watched source '{pinned.Path}' changed: pinned {pinned.Sha256}, found moved; "
                + "review the code that depends on it: opencode-sdk-dotnet: the door's dependency on zeta; opencode-agents-ai: the door's dependency on alpha",
            $"watched source '{pinned.Path}' no longer carries its anchor (contains 'zeta') for opencode-sdk-dotnet: the door's dependency on zeta",
            $"watched source '{pinned.Path}' no longer carries its anchor (contains 'alpha') for opencode-agents-ai: the door's dependency on alpha",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("text", false)]
    [Arguments("text", true)]
    [Arguments("verdict", false)]
    [Arguments("verdict", true)]
    [Arguments("order", false)]
    [Arguments("order", true)]
    [Arguments("extra", false)]
    [Arguments("extra", true)]
    public async Task CompareReceipt_Should_Compare_All_Anchor_Texts_And_Verdicts_In_Order_In_Both_Directions(string fault, bool reverse)
    {
        var first = new ReceiptWatchedAnchor { Text = "zeta", Matched = true };
        var second = new ReceiptWatchedAnchor { Text = "alpha", Matched = false };
        var recorded = Observed(Pinned.Sha256, anchorMatched: true) with { Anchors = [first, second] };
        var changed = recorded with
        {
            Anchors = fault switch
            {
                "text" => [first, second with { Text = "other" }],
                "verdict" => [first, second with { Matched = true }],
                "order" => [second, first],
                _ => [first, second, new ReceiptWatchedAnchor { Text = "third", Matched = true }],
            },
        };

        var problems = SourceWatchVerifier.CompareReceipt(reverse ? [changed] : [recorded], reverse ? [recorded] : [changed]);

        await Assert.That(problems).IsEquivalentTo(
            [$"the receipt records watched source '{Pinned.Path}' with different anchors than this checkout observes"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
