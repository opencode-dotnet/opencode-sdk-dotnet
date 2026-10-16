using OpenCode.Sdk.Tools.Generator.Refresh;
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
            RefreshScenarioData.Watch(1, pinned), [RefreshScenarioData.Observation(pinned, moved, anchorMatched: true)]);

        await Assert.That(repinned.Sources.Single().Sha256).IsEqualTo(moved);
        await Assert.That(repinned.Sources.Single().Anchor.Text).IsEqualTo(pinned.Anchor.Text);
        await Assert.That(repinned.SchemaVersion).IsEqualTo(1);
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
            .That(() => SourceWatchRepinner.Repin(RefreshScenarioData.Watch(1, pinned), observed))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("re-run prepare");
    }

    [Test]
    public async Task Repin_Should_Refuse_A_Lost_Anchor()
    {
        var pinned = RefreshScenarioData.TicketSource;
        var exception = await Assert
            .That(() => SourceWatchRepinner.Repin(
                RefreshScenarioData.Watch(1, pinned), [RefreshScenarioData.Observation(pinned, pinned.Sha256, anchorMatched: false)]))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("lost anchors in packages/core/src/pty/ticket.ts");
    }

}
