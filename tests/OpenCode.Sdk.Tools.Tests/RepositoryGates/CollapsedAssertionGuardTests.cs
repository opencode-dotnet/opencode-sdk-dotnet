using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class CollapsedAssertionGuardTests
{
    [Test]
    [Arguments("Any")]
    [Arguments("All")]
    [Arguments("SequenceEqual")]
    [Arguments("SetEquals")]
    [Arguments("ReferenceEquals")]
    [Arguments("DeepEquals")]
    [Arguments("TrueForAll")]
    [Arguments("Exists")]
    [Arguments("Contains")]
    [Arguments("ContainsKey")]
    public async Task CollapsedAssertionGuard_Should_Report_A_Hit(string name)
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", $"Assert.That(xs.{name}(x)).IsTrue();").CorpusAsync();

        var sites = CollapsedAssertionGuard.Find(corpus);

        await Assert.That(sites.Select(static site => site.ToString())).Contains($"tests/P/A.cs:1: Rule A: {CollapsedAssertionGuard.Text}");
    }

    [Test]
    [Arguments("// Assert.That(xs.Any()).IsTrue();")]
    [Arguments("var s = \"Assert.That(xs.Any()).IsTrue();\";")]
    [Arguments("var s = \"\"\"Assert.That(xs.Any()).IsTrue();\"\"\";")]
    [Arguments("var s = $\"{Assert.That(xs.Any()).IsTrue()}\";")]
    [Arguments("Assert.That(xs.Any()).Because(why).IsTrue();")]
    [Arguments("Assert.That(value).IsFalse();")]
    public async Task CollapsedAssertionGuard_Should_Ignore_Nonmatching_Source(string source)
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", source).CorpusAsync();

        await Assert.That(CollapsedAssertionGuard.Find(corpus)).IsEmpty();
    }

    [Test]
    public async Task CollapsedAssertionGuard_Should_Report_A_Hit_When_Generic_And_Inactive()
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs",
            "#if NEVER_DEFINED\nAssert . That(\n xs.Any<T>() || ys.All(x)).IsFalse();\n#endif\n").CorpusAsync();

        await Assert.That(CollapsedAssertionGuard.Find(corpus).Select(static site => site.Line)).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CollapsedAssertionGuard_Should_Report_A_Hit_When_The_Invocation_Is_Conditional()
    {
        var corpus = await new RepositoryGateScenario().WithFile("tests/P/A.cs", "Assert.That(xs?.Any() ?? false).IsTrue();").CorpusAsync();

        await Assert.That(CollapsedAssertionGuard.Find(corpus).Select(static site => site.Line)).IsEquivalentTo([1], CollectionOrdering.Matching);
    }
}
