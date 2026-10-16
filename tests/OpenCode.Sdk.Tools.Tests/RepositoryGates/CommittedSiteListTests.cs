using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>Each guard's sites in the repository match its committed list exactly.</summary>
[ClassDataSource<TestSourceCorpusSession>(Shared = SharedType.PerTestSession)]
public sealed class CommittedSiteListTests(TestSourceCorpusSession session)
{
    [Test]
    public async Task CollapsedAssertionGuard_Should_Match_The_Committed_Site_List()
    {
        var sites = CollapsedAssertionGuard.Find(session.Corpus);
        await Assert.That(await session.CheckAsync("collapsed-assertions", sites, CollapsedAssertionGuard.Rule, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task OrderBlindEquivalenceGuard_Should_Match_The_Committed_Site_List()
    {
        var sites = OrderBlindEquivalenceGuard.Find(session.Corpus);
        await Assert.That(await session.CheckAsync("order-blind-equivalence", sites, OrderBlindEquivalenceGuard.Rule, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task RealClockGuard_Should_Match_The_Committed_Site_List()
    {
        var sites = RealClockGuard.Find(session.Corpus);
        await Assert.That(await session.CheckAsync("real-clock", sites, RealClockGuard.Rule, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task LiveFinallyGuard_Should_Match_The_Committed_Site_List()
    {
        var sites = LiveFinallyGuard.Find(session.Corpus);
        await Assert.That(await session.CheckAsync("live-finally", sites, LiveFinallyGuard.Rule, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task FileSizeGuard_Should_Match_The_Committed_Site_List()
    {
        var sites = FileSizeGuard.Find(session.Corpus);
        await Assert.That(await session.CheckAsync("file-size", sites, FileSizeGuard.Rule, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task MirrorGuard_Should_Match_The_Committed_Site_List()
    {
        var sites = MirrorGuard.Find(session.Corpus);
        await Assert.That(await session.CheckAsync("mirror", sites, MirrorGuard.Rule, CancellationToken.None)).IsEmpty();
    }
}
