using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class OrderBlindEquivalenceGuardTests
{
    [Test]
    [Arguments("xs.IsEquivalentTo(ys);")]
    [Arguments("xs?.IsEquivalentTo(ys);")]
    [Arguments("xs.IsEquivalentTo(\"CollectionOrdering\");")]
    [Arguments("xs.IsEquivalentTo(ys /* CollectionOrdering */);")]
    [Arguments("xs.IsEquivalentTo(otherCollectionOrdering);")]
    public async Task OrderBlindEquivalenceGuard_Should_Report_A_Hit(string source)
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", source).CorpusAsync();

        await Assert.That(OrderBlindEquivalenceGuard.Find(corpus).Select(static site => site.ToString()))
            .Contains($"tests/P/A.cs:1: Rule B: {OrderBlindEquivalenceGuard.Text}");
    }

    [Test]
    [Arguments("xs.IsEquivalentTo(ys, CollectionOrdering.Matching);")]
    [Arguments("// xs.IsEquivalentTo(ys);")]
    [Arguments("var s = \"xs.IsEquivalentTo(ys);\";")]
    [Arguments("var s = $\"{xs.IsEquivalentTo(ys)}\";")]
    public async Task OrderBlindEquivalenceGuard_Should_Ignore_Nonmatching_Source(string source)
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", source).CorpusAsync();

        await Assert.That(OrderBlindEquivalenceGuard.Find(corpus)).IsEmpty();
    }

    [Test]
    public async Task OrderBlindEquivalenceGuard_Should_Report_A_Hit_When_Calls_Share_A_Line()
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", "a.IsEquivalentTo(b); c.IsEquivalentTo(d);").CorpusAsync();

        await Assert.That(OrderBlindEquivalenceGuard.Find(corpus).Select(static site => site.Line)).IsEquivalentTo([1, 1], CollectionOrdering.Matching);
    }
}
