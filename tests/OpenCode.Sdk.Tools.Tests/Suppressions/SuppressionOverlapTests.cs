using OpenCode.Sdk.Tools.Tests.Suppressions.Support;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>One rule, one file, one channel: how each channel reaches files, and what counts as two.</summary>
public sealed class SuppressionOverlapTests
{
    private const string EditorConfig = ".editorconfig";
    private const string Root = "root = true";
    private const string Source = "src/P/A.cs";
    private const string OtherSource = "src/Q/B.cs";
    private const string Pragma = "#pragma warning disable CA1000";
    private const string Lowered = "dotnet_diagnostic.CA1000.severity = none";
    private const string Type = "internal sealed class A;";
    private const string PragmaAndEditorConfig = $"{Source}: CA1000 is suppressed through Pragma, EditorConfig";

    [Test]
    public async Task Find_Should_Report_A_Pragma_Under_An_EditorConfig_Section_That_Lowers_The_Rule()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(EditorConfig, Root, $"[{Source}]", Lowered)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([PragmaAndEditorConfig]);
    }

    [Test]
    public async Task Find_Should_Report_A_Pragma_Under_The_Repository_Wide_Section()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(EditorConfig, Root, "[*.cs]", Lowered)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([PragmaAndEditorConfig]);
    }

    [Test]
    public async Task Find_Should_Not_Report_A_Section_That_Reaches_Another_File()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(EditorConfig, Root, $"[{OtherSource}]", Lowered)
            .WithFile(Source, Pragma, Type)
            .WithFile(OtherSource, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEmpty();
    }

    [Test]
    public async Task Find_Should_Not_Report_A_Rule_A_Later_Section_Raises_Again()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(EditorConfig, Root, "[*.cs]", Lowered, $"[{Source}]", "dotnet_diagnostic.CA1000.severity = error")
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEmpty();
    }

    [Test]
    public async Task Find_Should_Report_A_Pragma_In_A_Project_That_Lists_The_Rule_In_NoWarn()
    {
        var inventory = await new SuppressionScenario()
            .WithFile("src/P/P.csproj", "<Project><PropertyGroup><NoWarn>$(NoWarn);CA1000</NoWarn></PropertyGroup></Project>")
            .WithFile("src/Q/Q.csproj", "<Project />")
            .WithFile(Source, Pragma, Type)
            .WithFile(OtherSource, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"{Source}: CA1000 is suppressed through Pragma, NoWarn"]);
    }

    [Test]
    public async Task Find_Should_Reach_A_File_A_Project_Links_From_Another_Folder()
    {
        var inventory = await new SuppressionScenario()
            .WithFile("tests/P/P.csproj", "<Project>", "<PropertyGroup><NoWarn>CA1000</NoWarn></PropertyGroup>", "<ItemGroup><Compile Include=\"../Shared/**/*.cs\" /></ItemGroup>", "</Project>")
            .WithFile("tests/Shared/Harness/X.cs", Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo(["tests/Shared/Harness/X.cs: CA1000 is suppressed through Pragma, NoWarn"]);
    }

    [Test]
    public async Task Find_Should_Reach_Every_Project_Below_A_Directory_Build_File()
    {
        var inventory = await new SuppressionScenario()
            .WithFile("src/Directory.Build.props", "<Project><PropertyGroup><NoWarn>CA1000</NoWarn></PropertyGroup></Project>")
            .WithFile("src/P/P.csproj", "<Project />")
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"{Source}: CA1000 is suppressed through Pragma, NoWarn"]);
    }

    [Test]
    public async Task Find_Should_Report_A_Pragma_Beside_A_Global_Config_Entry()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(".globalconfig", "is_global = true", Lowered)
            .WithFile(Source, Pragma, Type)
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo([$"{Source}: CA1000 is suppressed through Pragma, GlobalConfig"]);
    }

    [Test]
    public async Task Find_Should_Report_A_Slopwatch_Suppression_The_Baseline_Also_Covers()
    {
        var inventory = await new SuppressionScenario()
            .WithFile(Source, "internal sealed class A", "{", "    [SlopwatchSuppress(\"SW003\", \"r\")]", "    public void M() { }", "}")
            .WithFile(SuppressionScan.SlopwatchBaselinePath, $"{{ \"entries\": [ {{ \"ruleId\": \"SW003\", \"filePath\": \"{Source}\" }} ] }}")
            .ScanAsync();

        await Assert.That(SuppressionOverlap.Find(inventory)).IsEquivalentTo(
            [$"{Source}: SW003 is suppressed through SlopwatchSuppress, SlopwatchBaseline"]);
    }

    [Test]
    public async Task Find_Should_Refuse_A_Warning_List_In_An_Imported_File_It_Cannot_Attribute()
    {
        var inventory = await new SuppressionScenario()
            .WithFile("tests/Shared/Assets.targets", "<Project><PropertyGroup><NoWarn>CA1000</NoWarn></PropertyGroup></Project>")
            .ScanAsync();

        var exception = await Assert.That(() => SuppressionOverlap.Find(inventory)).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("tests/Shared/Assets.targets");
    }

    [Test]
    public async Task Scan_Should_Refuse_A_Compile_Include_Shape_It_Cannot_Resolve()
    {
        var scenario = new SuppressionScenario()
            .WithFile("tests/P/P.csproj", "<Project><ItemGroup><Compile Include=\"../Shared/*Harness*.cs\" /></ItemGroup></Project>");

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("../Shared/*Harness*.cs");
    }
}
