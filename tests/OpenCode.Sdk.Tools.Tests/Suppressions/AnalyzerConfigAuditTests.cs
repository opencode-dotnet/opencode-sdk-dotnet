using OpenCode.Sdk.Tools.Tests.Suppressions.Support;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>The shape rules for <c>.editorconfig</c> sections: one declaration per glob, and no section naming a file that is gone.</summary>
public sealed class AnalyzerConfigAuditTests
{
    private const string EditorConfig = ".editorconfig";
    private const string Rule = "dotnet_diagnostic.CA1000.severity = none";
    private const string IndentSize = "indent_size = 4";
    private const string LiveFile = "src/Here.cs";
    private const string LiveSection = "[src/Here.cs]";
    private const string Type = "internal sealed class Here;";

    [Test]
    public async Task FindDuplicateSections_Should_Name_Both_Lines_Of_A_Repeated_Glob()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig(LiveSection, Rule, LiveSection, "dotnet_diagnostic.CA1001.severity = none")
            .ScanAsync();

        var duplicates = AnalyzerConfigAudit.FindDuplicateSections(inventory.AnalyzerConfigs);

        await Assert.That(duplicates).IsEquivalentTo([$"{EditorConfig}: {LiveSection} at lines 2, 4"]);
    }

    [Test]
    public async Task FindUnmatchedSections_Should_Report_A_Path_Section_Whose_File_Is_Gone()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig(LiveSection, Rule, "[src/Gone.cs]", Rule)
            .WithFile(LiveFile, Type)
            .ScanAsync();

        var unmatched = AnalyzerConfigAudit.FindUnmatchedSections(inventory);

        await Assert.That(unmatched).IsEquivalentTo([$"{EditorConfig}:4: [src/Gone.cs] matches no file"]);
    }

    [Test]
    public async Task FindUnmatchedSections_Should_Report_A_File_Name_Section_Whose_File_Is_Gone()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig("[Here.cs]", Rule, "[Gone.cs]", Rule)
            .WithFile(LiveFile, Type)
            .ScanAsync();

        var unmatched = AnalyzerConfigAudit.FindUnmatchedSections(inventory);

        await Assert.That(unmatched).IsEquivalentTo([$"{EditorConfig}:4: [Gone.cs] matches no file"]);
    }

    [Test]
    public async Task FindUnmatchedSections_Should_Report_One_Gone_Alternative_Of_A_Brace_Group()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig("[src/{Here,Gone}.cs]", Rule)
            .WithFile(LiveFile, Type)
            .ScanAsync();

        var unmatched = AnalyzerConfigAudit.FindUnmatchedSections(inventory);

        await Assert.That(unmatched).IsEquivalentTo(
            [$"{EditorConfig}:2: [src/{{Here,Gone}}.cs] has an alternative, 'src/Gone.cs', that matches no file"]);
    }

    [Test]
    public async Task FindUnmatchedSections_Should_Keep_The_Anchoring_Of_The_Whole_Glob_For_Each_Alternative()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig("[{Top.cs,src/Here.cs}]", Rule)
            .WithFile(LiveFile, Type)
            .WithFile("deep/Top.cs", Type)
            .ScanAsync();

        var unmatched = AnalyzerConfigAudit.FindUnmatchedSections(inventory);

        await Assert.That(unmatched).IsEquivalentTo(
            [$"{EditorConfig}:2: [{{Top.cs,src/Here.cs}}] has an alternative, 'Top.cs', that matches no file"]);
    }

    [Test]
    public async Task FindUnmatchedSections_Should_Leave_A_File_Type_Section_Without_Instances_Alone()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig("[*.sh]", IndentSize, "[**/*.ps1]", IndentSize)
            .WithFile(LiveFile, Type)
            .ScanAsync();

        var unmatched = AnalyzerConfigAudit.FindUnmatchedSections(inventory);

        await Assert.That(unmatched).IsEquivalentTo([$"{EditorConfig}:4: [**/*.ps1] matches no file"]);
    }

    [Test]
    public async Task FindUnmatchedSections_Should_Match_A_Recursive_Glob_Below_The_Config()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig("[src/**/*.cs]", Rule)
            .WithFile("src/Deep/Er/Here.cs", Type)
            .ScanAsync();

        var unmatched = AnalyzerConfigAudit.FindUnmatchedSections(inventory);

        await Assert.That(unmatched).IsEmpty();
    }

    [Test]
    public async Task ExpandAlternatives_Should_Expand_Nested_Comma_Groups()
    {
        var alternatives = AnalyzerConfigAudit.ExpandAlternatives("{a,{b,c}}/x.cs");

        await Assert.That(alternatives).IsEquivalentTo(["a/x.cs", "b/x.cs", "c/x.cs"]);
    }

    [Test]
    public async Task ExpandAlternatives_Should_Expand_A_Comma_Group_Inside_A_Single_Alternative_Group()
    {
        var alternatives = AnalyzerConfigAudit.ExpandAlternatives("src/{{Here,Gone}}.cs");

        await Assert.That(alternatives).IsEquivalentTo(["src/{Here}.cs", "src/{Gone}.cs"]);
    }

    [Test]
    public async Task ExpandAlternatives_Should_Keep_A_Numeric_Range_As_Written()
    {
        const string range = "src/{1..3}.cs";

        var alternatives = AnalyzerConfigAudit.ExpandAlternatives(range);

        await Assert.That(alternatives).IsEquivalentTo([range]);
    }

    [Test]
    public async Task ExpandAlternatives_Should_Keep_Escaped_Braces_As_Written()
    {
        const string escaped = "src/\\{a,b\\}.cs";

        var alternatives = AnalyzerConfigAudit.ExpandAlternatives(escaped);

        await Assert.That(alternatives).IsEquivalentTo([escaped]);
    }

    [Test]
    public async Task ExpandAlternatives_Should_Keep_Braces_Inside_A_Character_Class_As_Written()
    {
        const string characterClass = "src/[{a,b}].cs";

        var alternatives = AnalyzerConfigAudit.ExpandAlternatives(characterClass);

        await Assert.That(alternatives).IsEquivalentTo([characterClass]);
    }
}
