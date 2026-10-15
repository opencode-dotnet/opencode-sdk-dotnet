using OpenCode.Sdk.Tools.Tests.Suppressions.Support;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>Each suppression channel the scan reads, and what it leaves alone.</summary>
public sealed class SuppressionScanTests
{
    private const string Source = "src/A.cs";
    private const string Project = "src/A.csproj";
    private const string Type = "internal sealed class A;";
    private const string TypeHeader = "internal sealed class A";
    private const string Pragma = "#pragma warning disable CA1000";
    private const string EditorConfig = ".editorconfig";
    private const string EditorConfigRoot = "root = true";
    private const string CSharpSection = "[*.cs]";

    [Test]
    public async Task Scan_Should_Count_Each_Rule_A_Pragma_Disables()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, "#pragma warning disable CA1000, S1075 // the reason", Type, "#pragma warning restore CA1000, S1075")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"pragma {Source} CA1000", $"pragma {Source} S1075"]);
    }

    [Test]
    public async Task Scan_Should_Count_A_Pragma_In_An_Inactive_Region()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, "#if NEVER_DEFINED", "#pragma warning disable MA0045 // a reason, with a comma", "#endif", Type)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"pragma {Source} MA0045"]);
    }

    [Test]
    public async Task Scan_Should_Count_A_Pragma_Without_Rules_As_Every_Rule()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, "#pragma warning disable", Type)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"pragma {Source} *"]);
    }

    [Test]
    public async Task Scan_Should_Not_Count_Pragma_Text_Inside_A_String()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, TypeHeader, "{", "    public const string Text = @\"", Pragma, "\";", "}")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task Scan_Should_Count_The_Rule_Of_Each_Suppress_Message_Attribute()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                Source,
                "[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(\"Design\", \"CA1000:Do not declare static members\", Justification = \"r\")]",
                "[UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"r\")]",
                Type)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"suppress-message {Source} CA1000", $"suppress-message {Source} IL2026"]);
    }

    [Test]
    public async Task Scan_Should_Count_A_Slopwatch_Suppression_Across_Lines()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, TypeHeader, "{", "    [SlopwatchSuppress(", "        \"SW003\",", "        \"r\")]", "    public void M() { }", "}")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"slopwatch-suppress {Source} SW003"]);
    }

    [Test]
    public async Task Scan_Should_Count_Slopwatch_Ignore_Comments_In_Code_And_Project_Files()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, "// slopwatch-ignore: SW004 the reason", Type)
            .WithFile(Project, "<Project>", "  <!-- slopwatch-ignore: SW005 the reason -->", "</Project>")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"slopwatch-ignore {Source} SW004", $"slopwatch-ignore {Project} SW005"]);
    }

    [Test]
    public async Task Scan_Should_Count_Warning_Lists_And_Analyzer_Switches_In_Project_Files()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                Project,
                "<Project>",
                "  <PropertyGroup>",
                "    <NoWarn>$(NoWarn);S1075;S2068</NoWarn>",
                "    <WarningsNotAsErrors>CA1000</WarningsNotAsErrors>",
                "    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>",
                "    <AnalysisModeSecurity>None</AnalysisModeSecurity>",
                "    <Nullable>enable</Nullable>",
                "  </PropertyGroup>",
                "</Project>")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(
        [
            $"no-warn {Project} S1075", $"no-warn {Project} S2068", $"warnings-not-as-errors {Project} CA1000",
            $"build-switch {Project} TreatWarningsAsErrors = false", $"build-switch {Project} AnalysisModeSecurity = None",
        ]);
    }

    [Test]
    public async Task Scan_Should_Count_A_Warning_List_In_A_File_Based_App_Directive()
    {
        var lines = await new SuppressionScenario()
            .WithFile("tools/app.cs", "#:property NoWarn=CA1000;CA1001", "#:property TargetFramework=net10.0", "System.Console.WriteLine();")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(["no-warn tools/app.cs CA1000", "no-warn tools/app.cs CA1001"]);
    }

    [Test]
    public async Task Scan_Should_Count_Every_EditorConfig_Entry_That_Lowers_A_Diagnostic()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                EditorConfig,
                EditorConfigRoot,
                CSharpSection,
                "indent_size = 4",
                "dotnet_diagnostic.CA1000.severity = none",
                "dotnet_diagnostic.CA1001.severity = silent",
                "dotnet_diagnostic.CA1002.severity = suggestion",
                "dotnet_diagnostic.CA1003.severity = warning",
                "dotnet_diagnostic.CA1004.severity = error",
                "dotnet_analyzer_diagnostic.category-roslynator.severity = suggestion",
                "csharp_style_var_elsewhere = true:suggestion",
                "csharp_style_namespace_declarations = file_scoped:error",
                "generated_code = true",
                "roslynator_analyzers.enabled_by_default = false",
                "MA0051.maximum_lines_per_method = 80")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(
        [
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1000.severity = none",
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1001.severity = silent",
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1002.severity = suggestion",
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_analyzer_diagnostic.category-roslynator.severity = suggestion",
            $"editorconfig {EditorConfig} {CSharpSection} csharp_style_var_elsewhere = true:suggestion",
            $"editorconfig {EditorConfig} {CSharpSection} generated_code = true",
            $"editorconfig {EditorConfig} {CSharpSection} roslynator_analyzers.enabled_by_default = false",
            $"editorconfig {EditorConfig} {CSharpSection} MA0051.maximum_lines_per_method = 80",
        ]);
    }

    [Test]
    public async Task Scan_Should_Count_A_Global_Config_Entry_On_Its_Own_Channel()
    {
        var lines = await new SuppressionScenario()
            .WithFile(".globalconfig", "is_global = true", "dotnet_diagnostic.VSTHRD010.severity = none")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(["globalconfig .globalconfig dotnet_diagnostic.VSTHRD010.severity = none"]);
    }

    [Test]
    public async Task Scan_Should_Refuse_An_EditorConfig_Line_It_Cannot_Read()
    {
        var scenario = new SuppressionScenario().WithFile(EditorConfig, EditorConfigRoot, CSharpSection, "this is not a property");

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(EditorConfig + ":3");
    }

    [Test]
    public async Task Scan_Should_Scope_A_Slopwatch_Baseline_Entry_To_The_File_It_Names()
    {
        var lines = await new SuppressionScenario()
            .WithFile(SuppressionScan.SlopwatchBaselinePath, "{ \"version\": 1, \"entries\": [ { \"ruleId\": \"SW003\", \"filePath\": \"src\\\\A.cs\", \"lineNumber\": 4 } ] }")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"slopwatch-baseline {SuppressionScan.SlopwatchBaselinePath} [{Source}] SW003"]);
    }

    [Test]
    public async Task Scan_Should_Skip_Build_Output_And_Upstream_Checkouts()
    {
        var lines = await new SuppressionScenario()
            .WithFile("src/bin/A.cs", Pragma)
            .WithFile("tests/P/obj/B.cs", Pragma)
            .WithFile("external/upstream/C.cs", Pragma)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task Ledger_Should_Count_A_Repeated_Suppression_On_One_Line()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, "#pragma warning disable MA0045", "#pragma warning restore MA0045", "#pragma warning disable MA0045")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"pragma {Source} MA0045 x2"]);
    }
}
