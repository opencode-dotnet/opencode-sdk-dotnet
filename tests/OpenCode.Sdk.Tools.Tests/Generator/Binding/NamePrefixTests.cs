using OpenCode.Sdk.Tools.Generator.Binding;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// The rule model ownership and Sessions areas share: a prefix matches only where the name starts
/// a new word after it, so a family's prefix never claims a longer word that merely begins with it.
/// </summary>
public sealed class NamePrefixTests
{
    [Test]
    [Arguments("SessionFork", "SessionFork")]
    [Arguments("SessionForkRequest", "SessionFork")]
    [Arguments("Websearch2Result", "Websearch")]
    [Arguments("WebSearchResult", "Websearch")]
    [Arguments("WebSearch", "Websearch")]
    public async Task Matches_Should_Match_When_The_Prefix_Ends_At_A_Word_Boundary(string name, string prefix)
    {
        await Assert.That(NamePrefix.Matches(name, prefix)).IsTrue();
    }

    [Test]
    [Arguments("SessionForked", "SessionFork")]
    [Arguments("Session", "SessionFork")]
    [Arguments("ProjectForkRequest", "SessionFork")]
    public async Task Matches_Should_Not_Match_When_The_Name_Does_Not_Start_With_The_Prefix_As_A_Whole_Word(string name, string prefix)
    {
        await Assert.That(NamePrefix.Matches(name, prefix)).IsFalse();
    }
}
