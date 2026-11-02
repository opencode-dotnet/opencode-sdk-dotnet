using System.Xml.Linq;
using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class PlaygroundGuardTests
{
    private const string ProjectPath = "playground/QuickStart/nested/App.csproj";

    [Test]
    public async Task PlaygroundGuard_Should_Keep_The_Repository_Playground_In_The_Solution_And_Documented()
    {
        var fileSystem = new RealFileSystem();
        var violations = await new PlaygroundGuard(fileSystem).FindAsync(new RepositoryRoot(fileSystem).Locate());

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments("", true, "[app](QuickStart)", "playground/QuickStart/nested/App.csproj: missing Project Path in OpenCode.slnx.")]
    [Arguments("playground\\QuickStart\\nested\\App.csproj", true, "[app](QuickStart/)", "")]
    [Arguments(ProjectPath, false, "[app](./QuickStart)", "playground/QuickStart/README.md: missing README.")]
    [Arguments(ProjectPath, true, "[app](QuickStartOther/)", "playground/QuickStart: missing inline link in playground/README.md.")]
    [Arguments(ProjectPath, true, "[app][sample]\n[sample]: QuickStart/", "playground/QuickStart: missing inline link in playground/README.md.")]
    [Arguments(ProjectPath, true, "[app](https://example.com/QuickStart/)", "playground/QuickStart: missing inline link in playground/README.md.")]
    [Arguments(ProjectPath, true, "![app](QuickStart/arch.svg)", "playground/QuickStart: missing inline link in playground/README.md.")]
    public async Task PlaygroundGuard_Should_Check_Each_Requirement(string solutionPath, bool readme, string index, string expected)
    {
        var scenario = new RepositoryGateScenario()
            .WithFile("OpenCode.slnx", Solution(string.IsNullOrEmpty(solutionPath) ? [] : [solutionPath]))
            .WithFile(ProjectPath)
            .WithFile("playground/README.md", index);
        if (readme)
        {
            scenario.WithFile("playground/QuickStart/README.md");
        }

        await scenario.BuildAsync();
        var violations = await new PlaygroundGuard(scenario.FileSystem).FindAsync(scenario.Root);

        string[] messages = string.IsNullOrEmpty(expected) ? [] : [expected];
        await Assert.That(violations).IsEquivalentTo(messages, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PlaygroundGuard_Should_Ignore_A_Leftover_Folder_Without_A_Project()
    {
        var scenario = new RepositoryGateScenario()
            .WithFile("OpenCode.slnx", Solution())
            .WithFile("playground/Leftover/obj/x.json")
            .WithFile("playground/README.md");
        await scenario.BuildAsync();

        var violations = await new PlaygroundGuard(scenario.FileSystem).FindAsync(scenario.Root);

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    public async Task PlaygroundGuard_Should_Report_Two_Apps_In_Lexical_Order(bool readmes, bool completeSolution)
    {
        var scenario = new RepositoryGateScenario()
            .WithFile("OpenCode.slnx", Solution(completeSolution
                ? ["playground/Alpha/Alpha.csproj", "playground/Zed/Zed.csproj"]
                : ["playground/Alpha/Alpha.csproj"]))
            .WithFile("playground/Zed/Zed.csproj")
            .WithFile("playground/Alpha/Alpha.csproj")
            .WithFile("playground/README.md", "[Zed](./Zed/source) [Alpha](Alpha)");
        if (readmes || !completeSolution)
        {
            scenario.WithFile("playground/Zed/README.md");
        }
        if (readmes)
        {
            scenario.WithFile("playground/Alpha/README.md");
        }

        await scenario.BuildAsync();
        var violations = await new PlaygroundGuard(scenario.FileSystem).FindAsync(scenario.Root);

        string[] messages = (readmes, completeSolution) switch
        {
            (true, true) => [],
            (false, true) => ["playground/Alpha/README.md: missing README.", "playground/Zed/README.md: missing README."],
            (true, false) => ["playground/Zed/Zed.csproj: missing Project Path in OpenCode.slnx."],
            (false, false) => ["playground/Alpha/README.md: missing README.", "playground/Zed/Zed.csproj: missing Project Path in OpenCode.slnx."],
        };
        await Assert.That(violations).IsEquivalentTo(messages, CollectionOrdering.Matching);
    }

    private static string Solution(params string[] projectPaths) =>
        new XElement("Solution", projectPaths.Select(static path => new XElement("Project", new XAttribute("Path", path)))).ToString();
}
