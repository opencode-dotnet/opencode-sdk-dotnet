using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class MirrorGuardTests
{
    [Test]
    [Arguments("tests/OpenCode.Sdk.Tools.Tests/", "tools/OpenCode.Sdk.Tools/")]
    [Arguments("tests/OpenCode.Sdk.Tests/", "src/OpenCode.Sdk/")]
    public async Task MirrorGuard_Should_Report_A_Hit(string tests, string source)
    {
        var corpus = await new RepositoryGateScenario().WithFile(tests + "Wrong/XTests.cs", "// contents do not select a subject")
            .WithFile(source + "Internal/Right/Internal/X.Part.cs")
            .WithFile(source + "Right/X.Other.cs").CorpusAsync();

        await Assert.That(MirrorGuard.Find(corpus).Select(static site => site.ToString()))
            .Contains($"{tests}Wrong/XTests.cs:1: Mirror: {MirrorGuard.Text} Expected folder 'Right'.");
    }

    [Test]
    [Arguments("Right", "Right", "Right")]
    [Arguments("Wrong", "Right", "Other")]
    public async Task MirrorGuard_Should_Ignore_Nonmatching_Source(string testFolder, string firstHome, string secondHome)
    {
        var corpus = await new RepositoryGateScenario().WithFile($"tests/OpenCode.Sdk.Tools.Tests/{testFolder}/XTests.cs")
            .WithFile($"tools/OpenCode.Sdk.Tools/{firstHome}/X.cs")
            .WithFile($"tools/OpenCode.Sdk.Tools/{secondHome}/X.Part.cs")
            .WithFile("tests/OpenCode.Sdk.Tools.Tests/UnknownTests.cs", "var s = \"XTests.cs\";").CorpusAsync();

        await Assert.That(MirrorGuard.Find(corpus)).IsEmpty();
    }

    [Test]
    public async Task MirrorGuard_Should_Report_A_Hit_When_Only_The_Test_Folder_Has_Internal()
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/OpenCode.Sdk.Tests/Internal/XTests.cs")
            .WithFile("src/OpenCode.Sdk/Internal/X.cs").CorpusAsync();

        await Assert.That(MirrorGuard.Find(corpus).Select(static site => site.Detail)).Contains($"{MirrorGuard.Text} Expected folder ''.");
    }
}
