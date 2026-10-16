using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class TestSourceCorpusTests
{
    [Test]
    public async Task RepositoryFileSet_Should_Read_Only_Git_Listed_Files()
    {
        var scenario = new RepositoryGateScenario()
            .WithFile("tests/Z space.cs")
            .WithFile("bin/A.cs")
            .WithFile("ignored.cs")
            .WithFile("external/upstream/child.cs")
            .WithGitFiles("tests/Z space.cs", "bin\\A.cs", "external/upstream", "deleted.cs", "bin/A.cs");

        await scenario.BuildAsync();
        var files = await new RepositoryFileSet(scenario.FileSystem).EnumerateAsync(scenario.Root, scenario.Runner, CancellationToken.None);

        await Assert.That(files).IsEquivalentTo(["bin/A.cs", "tests/Z space.cs"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RepositoryFileSet_Should_Report_Git_Failure()
    {
        var scenario = new RepositoryGateScenario();
        scenario.Runner.Expect("git", "ls-files", ScriptedProcessRunner.Fail("index unavailable"));

        var exception = await Assert.That(async () => await new RepositoryFileSet(scenario.FileSystem)
            .EnumerateAsync(scenario.Root, scenario.Runner, CancellationToken.None)).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("index unavailable");
    }

    [Test]
    public async Task TestSourceCorpus_Should_Preserve_Active_And_Inactive_Line_Offsets()
    {
        const string path = "tests/Shared/A.cs";
        var scenario = new RepositoryGateScenario().WithFile(path, "class A {}\n#if NEVER_DEFINED\nclass B {}\n#endif\n")
            .WithFile("src/A.cs", "not parsed");
        await scenario.BuildAsync();

        var corpus = await new TestSourceCorpusReader(scenario.FileSystem).ReadAsync(scenario.Root, [path, "src/A.cs"], CancellationToken.None);

        await Assert.That(corpus.Files).Count().IsEqualTo(1);
        await Assert.That(corpus.Files[0].Regions.Select(static region => region.LineOffset))
            .IsEquivalentTo([0, 2], CollectionOrdering.Matching);
        await Assert.That(corpus.Files[0].Regions.SelectMany(static region => region.Root.Members).Select(static member => member.ToString()))
            .IsEquivalentTo(["class A {}", "class B {}"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TestSourceCorpusSession_Should_Expose_One_Completed_Result()
    {
        const string path = "tests/P/A.cs";
        var scenario = new RepositoryGateScenario().WithFile(path, "class A {}").WithGitFiles(path);
        await scenario.BuildAsync();
        var session = new TestSourceCorpusSession(scenario.FileSystem, scenario.Runner, scenario.Root);
        await Assert.That(() => session.Corpus).Throws<InvalidOperationException>();

        await session.InitializeAsync();
        var corpus = session.Corpus;
        scenario.FileSystem.File.Delete(scenario.FileSystem.Path.Combine(scenario.Root, path));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => await Assert.That(session.Corpus).IsSameReferenceAs(corpus)));
        await Assert.That(session.Corpus.Files[0].Text.ToString()).IsEqualTo("class A {}");
        await Assert.That(scenario.Runner.Invocations).Count().IsEqualTo(1);
    }
}
