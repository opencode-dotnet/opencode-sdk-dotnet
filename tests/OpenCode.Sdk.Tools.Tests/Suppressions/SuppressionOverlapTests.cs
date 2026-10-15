using OpenCode.Sdk.Tools.Tests.Suppressions.Support;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>One rule, one file, one channel: how each channel reaches files, and what counts as two.</summary>
public sealed class SuppressionOverlapTests
{
    private const string Source = "src/P/A.cs";
    private const string OtherSource = "src/Q/B.cs";
    private const string ProjectP = "src/P/P.csproj";
    private const string Pragma = "#pragma warning disable CA1000";
    private const string Lowered = "dotnet_diagnostic.CA1000.severity = none";
    private const string Raised = "dotnet_diagnostic.CA1000.severity = error";
    private const string Rule = "CA1000";
    private const string Type = "internal sealed class A;";
    private const string AllSources = "[*.cs]";
    private const string GlobalConfig = ".globalconfig";
    private const string GlobalMarker = "is_global = true";
    private const string PragmaAndEditorConfig = $"{Source}: {Rule} is suppressed through Pragma, EditorConfig";
    private const string PragmaAndNoWarn = $"{Source}: {Rule} is suppressed through Pragma, NoWarn";

    [Test]
    public async Task Find_Should_Report_A_Pragma_Under_An_EditorConfig_Section_That_Lowers_The_Rule()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig($"[{Source}]", Lowered)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([PragmaAndEditorConfig]);
    }

    [Test]
    public async Task Find_Should_Report_A_Pragma_Under_The_Repository_Wide_Section()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig(AllSources, Lowered)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([PragmaAndEditorConfig]);
    }

    [Test]
    public async Task Find_Should_Not_Report_A_Section_That_Reaches_Another_File()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig($"[{OtherSource}]", Lowered)
            .WithFile(Source, Pragma, Type)
            .WithFile(OtherSource, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEmpty();
    }

    [Test]
    public async Task Find_Should_Not_Report_A_Rule_A_Later_Section_Raises_Again()
    {
        var inventory = await new SuppressionScenario()
            .WithEditorConfig(AllSources, Lowered, $"[{Source}]", Raised)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEmpty();
    }

    [Test]
    public async Task Find_Should_Report_A_Pragma_In_A_Project_That_Lists_The_Rule_In_NoWarn()
    {
        var inventory = await new SuppressionScenario()
            .WithProject(ProjectP, noWarn: Rule)
            .WithProject("src/Q/Q.csproj")
            .WithFile(Source, Pragma, Type)
            .WithFile(OtherSource, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([PragmaAndNoWarn]);
    }

    [Test]
    public async Task Find_Should_Match_A_Numeric_Pragma_And_A_Prefixed_Warning_List_Entry()
    {
        var inventory = await new SuppressionScenario()
            .WithProject(ProjectP, noWarn: "CS0618")
            .WithFile(Source, "#pragma warning disable 618", Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"{Source}: CS0618 is suppressed through Pragma, NoWarn"]);
    }

    [Test]
    public async Task Find_Should_Reach_A_Nested_Project_s_Files_From_The_Outer_Project()
    {
        var inventory = await new SuppressionScenario()
            .WithProject(ProjectP, noWarn: Rule)
            .WithProject("src/P/Nested/Q.csproj")
            .WithFile("src/P/Nested/A.cs", Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"src/P/Nested/A.cs: {Rule} is suppressed through Pragma, NoWarn"]);
    }

    [Test]
    [Arguments("../Shared/**/*.cs")]
    [Arguments("../Shared/*Harness*.cs")]
    public async Task Find_Should_Reach_A_File_A_Project_Links_From_Another_Folder(string include)
    {
        var inventory = await new SuppressionScenario()
            .WithProject("tests/P/P.csproj", noWarn: Rule, compileInclude: include)
            .WithFile("tests/Shared/Harness/X.cs", Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"tests/Shared/Harness/X.cs: {Rule} is suppressed through Pragma, NoWarn"]);
    }

    [Test]
    public async Task Find_Should_Reach_Every_File_Below_A_Directory_Build_File()
    {
        var inventory = await new SuppressionScenario()
            .WithFile("src/Directory.Build.props", "<Project><PropertyGroup><NoWarn>CA1000</NoWarn></PropertyGroup></Project>")
            .WithProject(ProjectP)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([PragmaAndNoWarn]);
    }

    [Test]
    public async Task Find_Should_Reach_The_Whole_Repository_From_An_Imported_File()
    {
        var inventory = await new SuppressionScenario()
            .WithFile("tests/Shared/Assets.targets", "<Project><PropertyGroup><NoWarn>CA1000</NoWarn></PropertyGroup></Project>")
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([PragmaAndNoWarn]);
    }

    [Test]
    public async Task Find_Should_Report_A_Pragma_Beside_A_Global_Config_Entry()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(GlobalConfig, GlobalMarker, "dotnet_diagnostic.ca1000.severity = none")
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"{Source}: {Rule} is suppressed through Pragma, GlobalConfig"]);
    }

    [Test]
    public async Task Find_Should_Report_A_Global_Config_Entry_An_EditorConfig_Section_Repeats()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(GlobalConfig, GlobalMarker, Lowered)
            .WithEditorConfig(AllSources, Lowered)
            .WithFile(Source, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"{Source}: {Rule} is suppressed through EditorConfig, GlobalConfig"]);
    }

    [Test]
    public async Task Find_Should_Not_Report_A_Global_Config_Entry_An_EditorConfig_Section_Raises_Again()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(GlobalConfig, GlobalMarker, Lowered)
            .WithEditorConfig(AllSources, Raised)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEmpty();
    }

    [Test]
    public async Task Find_Should_Report_A_Slopwatch_Suppression_The_Baseline_Also_Covers()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(Source, "internal sealed class A", "{", "    [SlopwatchSuppress(\"SW003\", \"r\")]", "    public void M() { }", "}")
            .WithBaselineEntry("SW003", Source, "9f2c")
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo(
            [$"{Source}: SW003 is suppressed through SlopwatchSuppress, SlopwatchBaseline"]);
    }
}
