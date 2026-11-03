using System.Globalization;
using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

public sealed class SnippetCompileGuardTests
{
    private const string MissingMember = "CS0117: 'OpenCodeClient' does not contain a definition for 'Missing'";
    private static readonly SnippetCompileGuard.SnippetCompiler Compiler = new(new RealFileSystem());

    [Test]
    public async Task FindAsync_Should_Compile_The_Repository_Snippets()
    {
        var fileSystem = new RealFileSystem();
        var violations = await new SnippetCompileGuard(fileSystem, Compiler).FindAsync(new RepositoryRoot(fileSystem).Locate());

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments("README.md", "using var local = new OpenCodeClient(new OpenCodeClientOptions());", "")]
    [Arguments("README.md", "OpenCodeClient.Missing();", MissingMember)]
    [Arguments("README.md", "Console.Write(typeof(OpenCode.Sdk.OpenCodeClient));", "")]
    [Arguments("README.md", "Console.Write(typeof(TUnit.Core.TestAttribute));", "CS0246: The type or namespace name 'TUnit' could not be found (are you missing a using directive or an assembly reference?)")]
    [Arguments("README.md", "Console.Write(typeof(Microsoft.Extensions.Hosting.Host));", "")]
    [Arguments("README.md", "Console.Write(typeof(Microsoft.Extensions.DependencyModel.DependencyContext));", "CS0234: The type or namespace name 'DependencyModel' does not exist in the namespace 'Microsoft.Extensions' (are you missing an assembly reference?)")]
    [Arguments("docs/guide/example.md", "using var local = new OpenCodeClient(new OpenCodeClientOptions());", "")]
    [Arguments("docs/guide/example.md", "OpenCodeClient.Missing();", MissingMember)]
    [Arguments("README.md", "await session.GetAsync();", "")]
    [Arguments("README.md", "await session.Missing();", "CS1061: 'SessionClient' does not contain a definition for 'Missing' and no accessible extension method 'Missing' accepting a first argument of type 'SessionClient' could be found (are you missing a using directive or an assembly reference?)")]
    [Arguments("README.md", "var session = string.Empty; Console.Write(session.Length);", "")]
    [Arguments("README.md", "var session = string.Empty; Console.Write(session.Missing);", "CS1061: 'string' does not contain a definition for 'Missing' and no accessible extension method 'Missing' accepting a first argument of type 'string' could be found (are you missing a using directive or an assembly reference?)")]
    [Arguments("README.md", "Console.Write(client.Experimental);", "")]
    [Arguments("README.md", "ExperimentalClient.Missing();", "CS0117: 'ExperimentalClient' does not contain a definition for 'Missing'")]
    [Arguments("README.md", "Console.Write(client.PersistentPtys);", "")]
    [Arguments("README.md", "PersistentPtysClient.Missing();", "CS0117: 'PersistentPtysClient' does not contain a definition for 'Missing'")]
    [Arguments("README.md", "using System.Text;\nConsole.Write(new StringBuilder());", "")]
    [Arguments("README.md", "using System.Text;\nStringBuilder.Missing();", "CS0117: 'StringBuilder' does not contain a definition for 'Missing'", 3)]
    [Arguments("README.md", "using System.Text;\nConsole.Write(1);", "")]
    [Arguments("README.md", "using System.Missing;\nConsole.Write(1);", "CS0234: The type or namespace name 'Missing' does not exist in the namespace 'System' (are you missing an assembly reference?)")]
    [Arguments("README.md", "using System.Text;\nusing System.Missing;\nConsole.Write(1);", "CS0234: The type or namespace name 'Missing' does not exist in the namespace 'System' (are you missing an assembly reference?)", 3)]
    [Arguments("README.md", "using var session = new CancellationTokenSource(); session.Cancel();", "")]
    [Arguments("README.md", "using var session = new CancellationTokenSource(); session.Missing();", "CS1061: 'CancellationTokenSource' does not contain a definition for 'Missing' and no accessible extension method 'Missing' accepting a first argument of type 'CancellationTokenSource' could be found (are you missing a using directive or an assembly reference?)")]
    [Arguments("README.md", "internal sealed class Named { }", "")]
    [Arguments("README.md", "internal sealed class Named : MissingType { }", "CS0246: The type or namespace name 'MissingType' could not be found (are you missing a using directive or an assembly reference?)")]
    [Arguments("README.md", "Console.Write(1);\ninternal sealed class Named { }", "mixed statements and type declarations must use separate fences.", 1)]
    [Arguments("README.md", "Console.Write(1);\n```\n```csharp\ninternal sealed class Named { }", "")]
    [Arguments("README.md", "int unused = 1;", "")]
    [Arguments("README.md", "int unused = \"x\";", "CS0029: Cannot implicitly convert type 'string' to 'int'")]
    [Arguments("README.md", "int[] values = [1]; Console.Write(values.Length);", "")]
    [Arguments("README.md", "int[] values = [\"x\"]; Console.Write(values.Length);", "CS0029: Cannot implicitly convert type 'string' to 'int'")]
    [Arguments("README.md", "if (true) {\nConsole.Write(1);", "CS1513: } expected", 1)]
    [Arguments("README.md", "if (true) {\nOpenCodeClient.Missing();\n}", MissingMember, 3)]
    public async Task FindAsync_Should_Compile_Selected_Pages(string page, string source, string error, int errorLine = 2)
    {
        var scenario = new RepositoryGateScenario().WithFile("README.md").WithSnippet(page, source);
        var violations = await FindAsync(scenario);

        string[] expected = string.IsNullOrEmpty(error) ? [] : [string.Create(CultureInfo.InvariantCulture, $"{page}:{errorLine}: {error}")];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task FindAsync_Should_Skip_Only_Marked_Fragments(bool marked)
    {
        var scenario = new RepositoryGateScenario().WithFile("README.md");
        if (marked)
        {
            scenario.WithSnippetMarker("README.md");
        }
        scenario.WithSnippet("README.md", "break;");
        var violations = await FindAsync(scenario);

        string[] expected = marked ? [] : ["README.md:2: CS0139: No enclosing loop out of which to break or continue"];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("csharp  ", "return;", "")]
    [Arguments("csharp  ", "OpenCodeClient.Missing();", "README.md:2: " + MissingMember)]
    [Arguments("cs", "return;", "README.md:1: write csharp instead of 'cs' for a C# fence.")]
    [Arguments("c#", "return;", "README.md:1: write csharp instead of 'c#' for a C# fence.")]
    [Arguments("CSharp", "return;", "README.md:1: write csharp instead of 'CSharp' for a C# fence.")]
    public async Task FindAsync_Should_Require_Trimmed_CSharp_Info(string info, string source, string error)
    {
        var scenario = new RepositoryGateScenario().WithSnippet("README.md", source, info);
        var violations = await FindAsync(scenario);

        string[] expected = string.IsNullOrEmpty(error) ? [] : [error];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("```csharp\nreturn;\n```", true)]
    [Arguments("```csharp  \nreturn;\n```", true)]
    [Arguments("```text\nreturn;\n```", false)]
    [Arguments("\n```csharp\nreturn;\n```", false)]
    [Arguments("", false)]
    public async Task FindAsync_Should_Require_An_Adjacent_CSharp_Fence(string following, bool adjacent)
    {
        var scenario = new RepositoryGateScenario().WithFile("README.md", "<!-- snippet: fragment -->\n" + following);
        var violations = await FindAsync(scenario);

        string[] expected = adjacent ? [] : ["README.md:1: fragment marker must be directly followed by a csharp fence."];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("README.md")]
    [Arguments("docs/guide/other.md")]
    public async Task FindAsync_Should_Share_Types_Only_Within_Their_Page(string declarationPage)
    {
        var scenario = new RepositoryGateScenario()
            .WithSnippet("README.md", "Console.Write(new Named());")
            .WithSnippet(declarationPage, "internal sealed class Named { }");
        var violations = await FindAsync(scenario);

        string[] expected = declarationPage == "README.md" ? [] :
            ["README.md:2: CS0246: The type or namespace name 'Named' could not be found (are you missing a using directive or an assembly reference?)"];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("README.md", "```csharp", "OpenCodeClient.Missing();", true)]
    [Arguments("docs/architecture/example.md", "```csharp", "OpenCodeClient.Missing();", false)]
    [Arguments("README.md", "```csharp extra", "OpenCodeClient.Missing();", false)]
    [Arguments("README.md", "  ```csharp", "return;", false)]
    [Arguments("README.md", "  ```csharp", "OpenCodeClient.Missing();", true)]
    [Arguments("README.md", "   ```csharp", "return;", false)]
    [Arguments("README.md", "   ```csharp", "OpenCodeClient.Missing();", true)]
    public async Task FindAsync_Should_Select_Fences(string page, string opening, string source, bool error)
    {
        var scenario = new RepositoryGateScenario().WithFile("README.md")
            .WithFile(page, opening + "\n" + source + "\n```");
        var violations = await FindAsync(scenario);

        string[] expected = error ? [$"{page}:2: {MissingMember}"] : [];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("Console.Write(1);", false)]
    [Arguments("OpenCodeClient.Missing();", true)]
    public async Task FindAsync_Should_Report_In_Page_And_Source_Order(string source, bool error)
    {
        var scenario = new RepositoryGateScenario().WithSnippet("docs/guide/z.md", source)
            .WithSnippet("README.md", source).WithSnippet("docs/guide/a.md", source).WithSnippet("README.md", source);
        var violations = await FindAsync(scenario);

        string[] expected = error ? [$"README.md:2: {MissingMember}", $"README.md:5: {MissingMember}",
            $"docs/guide/a.md:2: {MissingMember}", $"docs/guide/z.md:2: {MissingMember}"] : [];
        await Assert.That(violations).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static async Task<IReadOnlyList<string>> FindAsync(RepositoryGateScenario scenario)
    {
        await scenario.BuildAsync();
        return await new SnippetCompileGuard(scenario.FileSystem, Compiler).FindAsync(scenario.Root);
    }
}
