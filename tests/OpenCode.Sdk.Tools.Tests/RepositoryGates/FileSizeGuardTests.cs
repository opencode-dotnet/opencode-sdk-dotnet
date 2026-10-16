using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class FileSizeGuardTests
{
    [Test]
    [Arguments("\n", true)]
    [Arguments("\n", false)]
    [Arguments("\r\n", true)]
    [Arguments("\r\n", false)]
    public async Task FileSizeGuard_Should_Report_A_Hit(string newline, bool finalNewline)
    {
        var corpus = await new RepositoryGateScenario().WithPhysicalLines("tests/P/A.cs", 801, newline, finalNewline).CorpusAsync();

        await Assert.That(FileSizeGuard.Find(corpus).Select(static site => site.ToString()))
            .Contains($"tests/P/A.cs:801: File size: {FileSizeGuard.Text} Found 801 physical lines.");
    }

    [Test]
    [Arguments("\n", true)]
    [Arguments("\n", false)]
    [Arguments("\r\n", true)]
    [Arguments("\r\n", false)]
    public async Task FileSizeGuard_Should_Ignore_Nonmatching_Source(string newline, bool finalNewline)
    {
        var corpus = await new RepositoryGateScenario().WithPhysicalLines("tests/P/A.cs", 800, newline, finalNewline)
            .WithFile("tests/P/Empty.cs").CorpusAsync();

        await Assert.That(FileSizeGuard.Find(corpus)).IsEmpty();
    }
}
