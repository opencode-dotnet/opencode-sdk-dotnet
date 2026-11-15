using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SourceWatchRepinnerTests
{
    [Test]
    public async Task Repin_Should_Carry_The_Observed_Hashes_Onto_The_Watch()
    {
        var pinned = RefreshScenarioData.TicketSource;
        var moved = new string('c', 64);

        var repinned = SourceWatchRepinner.Repin(
            RefreshScenarioData.Watch(2, pinned), [RefreshScenarioData.Observation(pinned, moved, anchorMatched: true)]);

        await Assert.That(repinned.Sources.Single().Sha256).IsEqualTo(moved);
        await Assert.That(repinned.Sources.Single().Behaviors).IsSameReferenceAs(pinned.Behaviors);
        await Assert.That(repinned.SchemaVersion).IsEqualTo(2);
    }

    [Test]
    [Arguments("fewer")]
    [Arguments("more")]
    [Arguments("different")]
    public async Task Repin_Should_Refuse_A_Receipt_Observing_Other_Files(string variation)
    {
        var pinned = RefreshScenarioData.TicketSource;
        var observed = RefreshScenarioData.OtherSources(pinned, variation);
        var exception = await Assert
            .That(() => SourceWatchRepinner.Repin(RefreshScenarioData.Watch(2, pinned), observed))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("re-run prepare");
    }

    [Test]
    public async Task Repin_Should_Refuse_A_Lost_Anchor()
    {
        var pinned = RefreshScenarioData.TicketSource;
        var exception = await Assert
            .That(() => SourceWatchRepinner.Repin(
                RefreshScenarioData.Watch(2, pinned), [RefreshScenarioData.Observation(pinned, pinned.Sha256, anchorMatched: false)]))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("lost anchors in packages/core/src/pty/ticket.ts");
    }

    [Test]
    [Arguments("text")]
    [Arguments("order")]
    [Arguments("fewer")]
    [Arguments("more")]
    public async Task Repin_Should_Refuse_Anchor_Text_Drift(string fault)
    {
        var source = RefreshScenarioData.TicketSource with
        {
            Behaviors = [RefreshScenarioData.Behavior("zeta"), RefreshScenarioData.Behavior("alpha", WatchOwners.AgentsAi)],
        };
        var observation = RefreshScenarioData.Observation(source, "moved", anchorMatched: true);
        var anchors = observation.Anchors;
        observation = observation with
        {
            Anchors = fault switch
            {
                "text" => [anchors[0], anchors[1] with { Text = "other" }],
                "order" => [anchors[1], anchors[0]],
                "fewer" => [anchors[0]],
                _ => [.. anchors, new ReceiptWatchedAnchor { Text = "extra", Matched = true }],
            },
        };

        var exception = Assert.Throws<SnapshotRefreshException>(() => SourceWatchRepinner.Repin(
            RefreshScenarioData.Watch(2, source), [observation]));

        await Assert.That(exception.Message).IsEqualTo($"the receipt's anchor texts for '{source.Path}' differ from the source watch; re-run prepare");
    }

    [Test]
    public async Task Repin_Should_Name_Each_Lost_Anchors_Path_Text_And_Owner_In_Watch_Order()
    {
        var source = RefreshScenarioData.TicketSource with
        {
            Behaviors = [RefreshScenarioData.Behavior("zeta", WatchOwners.AgentsAi), RefreshScenarioData.Behavior("alpha")],
        };
        var other = RefreshScenarioData.PtyBufferSource;
        var exception = Assert.Throws<SnapshotRefreshException>(() => SourceWatchRepinner.Repin(RefreshScenarioData.Watch(2, source, other),
            [RefreshScenarioData.Observation(other, "moved", anchorMatched: false), RefreshScenarioData.Observation(source, "moved", anchorMatched: false)]));

        await Assert.That(exception.Message).IsEqualTo($"the receipt reports lost anchors in {source.Path} ('zeta', opencode-agents-ai), "
            + $"{source.Path} ('alpha', opencode-sdk-dotnet), {other.Path} ('BUFFER_LIMIT', opencode-sdk-dotnet); read the hand-written doors, "
            + "rewrite those anchors, then re-run prepare");
    }

}
