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
        await Assert.That(problems.Single()).Contains(Pinned.Behavior);
    }

    [Test]
    public async Task Compare_Should_Report_A_Lost_Anchor()
    {
        var problems = SourceWatchVerifier.Compare([Pinned], [Observed(Pinned.Sha256, anchorMatched: false)]);

        await Assert.That(problems.Single()).Contains("no longer carries its anchor (contains 'seconds(60)')");
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
            AnchorMatched = anchorMatched,
        };

    [Test]
    [Arguments("matching", null)]
    [Arguments("hash", "but this checkout carries")]
    [Arguments("anchor", "anchorMatched=true, but this checkout observes false")]
    [Arguments("recorded only", "which the source watch no longer names")]
    [Arguments("observed only", "which the receipt does not record")]
    public async Task CompareReceipt_Should_Report_The_Recorded_Observation_Difference(string variation, string? diagnostic)
    {
        var recorded = Observed(Pinned.Sha256, anchorMatched: true);
        var observed = variation switch
        {
            "hash" => recorded with { Sha256 = new string('b', 64) },
            "anchor" => recorded with { AnchorMatched = false },
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
}
