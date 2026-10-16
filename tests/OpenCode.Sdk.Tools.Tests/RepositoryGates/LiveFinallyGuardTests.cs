using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class LiveFinallyGuardTests
{
    [Test]
    public async Task LiveFinallyGuard_Should_Report_A_Hit()
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/ALiveTests.cs", "try {} finally {} ").CorpusAsync();

        await Assert.That(LiveFinallyGuard.Find(corpus).Select(static site => site.ToString()))
            .Contains($"tests/P/ALiveTests.cs:1: Live finally: {LiveFinallyGuard.Text}");
    }

    [Test]
    [Arguments("tests/P/ATests.cs", "try {} finally {}")]
    [Arguments("tests/P/ALiveTests.cs", "// finally")]
    [Arguments("tests/P/ALiveTests.cs", "var s = \"finally\";")]
    [Arguments("tests/P/ALiveTests.cs", "var s = $\"{Run(() => { try {} finally {} })}\";")]
    public async Task LiveFinallyGuard_Should_Ignore_Nonmatching_Source(string path, string source)
    {
        var corpus = await new RepositoryGateScenario().WithFile(path, source).CorpusAsync();

        await Assert.That(LiveFinallyGuard.Find(corpus)).IsEmpty();
    }

    [Test]
    public async Task LiveFinallyGuard_Should_Report_A_Hit_When_Clauses_Are_Nested()
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/ALiveTests.cs", "try {} finally { try {}\n finally {} }").CorpusAsync();

        await Assert.That(LiveFinallyGuard.Find(corpus).Select(static site => site.Line)).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
    }
}
