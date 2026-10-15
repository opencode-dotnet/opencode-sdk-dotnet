using OpenCode.Sdk.Tools.Tests.Suppressions.Support;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>Each suppression channel the scan reads, what it leaves alone, and each form it refuses.</summary>
public sealed class SuppressionScanTests
{
    private const string Source = "src/A.cs";
    private const string Project = "src/A.csproj";
    private const string Workflow = ".github/workflows/ci.yml";
    private const string Type = "internal sealed class A;";
    private const string TypeHeader = "internal sealed class A";
    private const string Pragma = "#pragma warning disable CA1000";
    private const string EditorConfig = ".editorconfig";
    private const string GlobalConfig = ".globalconfig";
    private const string CSharpSection = "[*.cs]";
    private const string ProjectOpen = "<Project>";
    private const string ProjectClose = "</Project>";
    private const string PropertyGroupOpen = "  <PropertyGroup>";
    private const string PropertyGroupClose = "  </PropertyGroup>";
    private const string Method = "    public void M() { }";
    private const string GlobalVsthrd010 = "dotnet_diagnostic.VSTHRD010.severity = none";

    [Test]
    public async Task Scan_Should_Count_Each_Rule_A_Pragma_Disables()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, "#pragma warning disable CA1000, S1075, 618 // the reason", Type, "#pragma warning restore CA1000, S1075, 618")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"pragma {Source} CA1000", $"pragma {Source} CS0618", $"pragma {Source} S1075"]);
    }

    [Test]
    public async Task Scan_Should_Count_A_Pragma_And_An_Attribute_In_An_Inactive_Region()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                Source,
                "#if NEVER_DEFINED",
                "#pragma warning disable MA0045 // a reason, with a comma",
                "[System.Diagnostics.CodeAnalysis.SuppressMessage(\"Design\", \"CA1001\")]",
                "#endif",
                Type)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"pragma {Source} MA0045", $"suppress-message {Source} CA1001"]);
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
    public async Task Scan_Should_Not_Count_Directive_Text_Inside_A_String()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, TypeHeader, "{", "    public const string Text = @\"", Pragma, "#:property NoWarn=CA1000", "\";", "}")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEmpty();
    }

    [Test]
    public async Task Scan_Should_Count_A_Nullable_Disable_Directive()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, "#nullable disable warnings", Type, "#nullable enable")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"nullable {Source} nullable = disable warnings"]);
    }

    [Test]
    public async Task Scan_Should_Count_The_Rule_Of_Each_Suppress_Message_Attribute_However_It_Is_Spelled()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                Source,
                "using Quiet = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;",
                "[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute(\"Design\", \"CA1000:Do not declare static members\", Justification = \"r\")]",
                "[UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"r\")]",
                "[SuppressMessage(category: \"Design\", checkId: \"CA1001\")]",
                "[Quiet(\"Design\", \"CA1002\")]",
                Type)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(
        [
            $"suppress-message {Source} CA1000", $"suppress-message {Source} CA1001", $"suppress-message {Source} CA1002",
            $"suppress-message {Source} IL2026",
        ]);
    }

    [Test]
    public async Task Scan_Should_Refuse_A_Rule_Given_Through_Anything_But_A_Literal()
    {
        var scenario = new SuppressionScenario()
            .WithFile(Source, TypeHeader, "{", "    private const string Rule = \"CA1000\";", "    [SuppressMessage(\"Design\", Rule)]", Method, "}");

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains($"{Source}:4").And.Contains("string literal");
    }

    [Test]
    public async Task Scan_Should_Refuse_An_Assembly_Level_Suppression()
    {
        var scenario = new SuppressionScenario()
            .WithFile(Source, "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Design\", \"CA1000\", Target = \"~T:A\")]", Type);

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains($"{Source}:1").And.Contains("assembly");
    }

    [Test]
    public async Task Scan_Should_Count_A_Slopwatch_Suppression_Across_Lines()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Source, TypeHeader, "{", "    [SlopwatchSuppress(", "        \"SW003\",", "        \"r\")]", Method, "}")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"slopwatch-suppress {Source} SW003"]);
    }

    [Test]
    public async Task Scan_Should_Count_Slopwatch_Ignore_Markers_As_Slopwatch_Reads_Them()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                Source,
                "// SLOPWATCH-IGNORE: sw004 the reason",
                "// slopwatch-ignore-start: SW003 the reason",
                "// slopwatch-ignore-end",
                "// An example: slopwatch-ignore SW001 is how a marker looks.",
                Type)
            .WithFile(Project, ProjectOpen, "  <!-- slopwatch-ignore: SW005 the reason -->", ProjectClose)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(
            [$"slopwatch-ignore {Source} SW003", $"slopwatch-ignore {Source} SW004", $"slopwatch-ignore {Project} SW005"]);
    }

    [Test]
    public async Task Scan_Should_Count_Warning_Lists_And_Analyzer_Switches_Under_Their_Conditions()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                Project,
                ProjectOpen,
                PropertyGroupOpen,
                "    <nowarn>$(NoWarn);S1075;S2068</nowarn>",
                "    <WarningsNotAsErrors>CA1000</WarningsNotAsErrors>",
                "    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>",
                "    <Nullable>disable</Nullable>",
                "    <RunAnalyzersDuringLiveAnalysis>false</RunAnalyzersDuringLiveAnalysis>",
                "    <OutputType>Exe</OutputType>",
                PropertyGroupClose,
                "  <PropertyGroup Condition=\"'$(TargetFramework)' == 'net472'\">",
                "    <NoWarn>$(NoWarn);CA2000</NoWarn>",
                PropertyGroupClose,
                ProjectClose)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(
        [
            $"no-warn {Project} S1075", $"no-warn {Project} S2068", $"no-warn {Project} ['$(TargetFramework)' == 'net472'] CA2000",
            $"warnings-not-as-errors {Project} CA1000", $"build-switch {Project} TreatWarningsAsErrors = false",
            $"build-switch {Project} Nullable = disable", $"build-switch {Project} RunAnalyzersDuringLiveAnalysis = false",
        ]);
    }

    [Test]
    public async Task Scan_Should_Refuse_A_Warning_List_That_Takes_Ids_From_Another_Property()
    {
        var scenario = new SuppressionScenario()
            .WithFile(Project, ProjectOpen, PropertyGroupOpen, "    <NoWarn>$(NoWarn);$(ExtraRules)</NoWarn>", PropertyGroupClose, ProjectClose);

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(Project).And.Contains("$(ExtraRules)");
    }

    [Test]
    public async Task Scan_Should_Refuse_A_Warning_List_On_An_Item()
    {
        var scenario = new SuppressionScenario()
            .WithFile(Project, ProjectOpen, "  <ItemGroup>", "    <PackageReference Include=\"P\" NoWarn=\"NU1903\" />", "  </ItemGroup>", ProjectClose);

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(Project).And.Contains("PackageReference");
    }

    [Test]
    public async Task Scan_Should_Count_A_Warning_List_Only_In_A_File_Based_App_Header()
    {
        var lines = await new SuppressionScenario()
            .WithFile(
                "tools/app.cs",
                "#!/usr/bin/env dotnet",
                "#:property NoWarn=CA1000;CA1001",
                "#:property TargetFramework=net10.0",
                "System.Console.WriteLine();",
                "#:property NoWarn=CA1002")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(["no-warn tools/app.cs CA1000", "no-warn tools/app.cs CA1001"]);
    }

    [Test]
    public async Task Scan_Should_Count_An_Analyzer_Switch_On_A_Workflow_Command_Line()
    {
        var lines = await new SuppressionScenario()
            .WithFile(Workflow, "jobs:", "  build:", "    steps:", "      - run: dotnet build -c Release -p:RunAnalyzers=false -p:IncludeNet472Tests=true")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"build-switch {Workflow} [command line] RunAnalyzers = false"]);
    }

    [Test]
    public async Task Scan_Should_Count_Every_EditorConfig_Entry_That_Lowers_A_Diagnostic()
    {
        var lines = await new SuppressionScenario()
            .WithEditorConfig(
                CSharpSection,
                "indent_size = 4",
                "dotnet_diagnostic.CA1000.severity = none",
                "dotnet_diagnostic.CA1001.severity = silent",
                "dotnet_diagnostic.CA1002.severity = suggestion",
                "dotnet_diagnostic.CA1003.severity = refactoring",
                "dotnet_diagnostic.CA1004.severity = warning",
                "dotnet_diagnostic.CA1005.severity = error",
                "dotnet_analyzer_diagnostic.category-roslynator.severity = suggestion",
                "csharp_style_var_elsewhere = true:suggestion",
                "csharp_style_namespace_declarations = file_scoped:error",
                "generated_code = true",
                "roslynator_analyzers.enabled_by_default = false",
                "MA0051.maximum_lines_per_method = 80",
                "DOTNET_CODE_QUALITY.CA1822.api_surface = private")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo(
        [
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1000.severity = none",
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1001.severity = silent",
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1002.severity = suggestion",
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1003.severity = refactoring",
            $"editorconfig {EditorConfig} {CSharpSection} dotnet_analyzer_diagnostic.category-roslynator.severity = suggestion",
            $"editorconfig {EditorConfig} {CSharpSection} csharp_style_var_elsewhere = true:suggestion",
            $"editorconfig {EditorConfig} {CSharpSection} generated_code = true",
            $"editorconfig {EditorConfig} {CSharpSection} roslynator_analyzers.enabled_by_default = false",
            $"editorconfig {EditorConfig} {CSharpSection} MA0051.maximum_lines_per_method = 80",
            $"editorconfig {EditorConfig} {CSharpSection} DOTNET_CODE_QUALITY.CA1822.api_surface = private",
        ]);
    }

    [Test]
    public async Task Scan_Should_Break_EditorConfig_Lines_On_Any_Line_Ending()
    {
        var lines = await new SuppressionScenario()
            .WithFile(EditorConfig, "# comment\r[*.cs]\rdotnet_diagnostic.CA1000.severity = none")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"editorconfig {EditorConfig} {CSharpSection} dotnet_diagnostic.CA1000.severity = none"]);
    }

    [Test]
    public async Task Scan_Should_Count_A_Global_Config_On_Its_Own_Channel()
    {
        var lines = await new SuppressionScenario()
            .WithFile(GlobalConfig, "is_global = true", GlobalVsthrd010)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"globalconfig {GlobalConfig} {GlobalVsthrd010}"]);
    }

    [Test]
    public async Task Scan_Should_Read_Any_Is_Global_Key_As_A_Global_Config()
    {
        var lines = await new SuppressionScenario()
            .WithFile("src/.editorconfig", "is_global = false", GlobalVsthrd010)
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"globalconfig src/.editorconfig {GlobalVsthrd010}"]);
    }

    [Test]
    public async Task Scan_Should_Refuse_An_EditorConfig_Line_It_Cannot_Read()
    {
        var scenario = new SuppressionScenario().WithEditorConfig(CSharpSection, "this is not a property");

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(EditorConfig + ":3");
    }

    [Test]
    public async Task Scan_Should_Refuse_A_Global_Config_With_Path_Sections()
    {
        var scenario = new SuppressionScenario().WithFile(GlobalConfig, "is_global = true", $"[{Source}]", GlobalVsthrd010);

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(GlobalConfig).And.Contains("path sections");
    }

    [Test]
    [Arguments("rules/Strict.ruleset")]
    [Arguments(".slopwatch/config.json")]
    public async Task Scan_Should_Refuse_A_Suppression_File_It_Does_Not_Read(string path)
    {
        var scenario = new SuppressionScenario().WithFile(path, "{}");

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(path);
    }

    [Test]
    public async Task Scan_Should_Scope_A_Slopwatch_Baseline_Entry_To_Its_File_With_Its_Hash()
    {
        var lines = await new SuppressionScenario()
            .WithBaselineEntry("SW003", "src\\\\A.cs", "9f2c")
            .LedgerLinesAsync();

        await Assert.That(lines).IsEquivalentTo([$"slopwatch-baseline {SuppressionScan.SlopwatchBaselinePath} [{Source}] SW003 = 9f2c"]);
    }

    [Test]
    public async Task Scan_Should_Refuse_A_Slopwatch_Baseline_Entry_Without_A_Hash()
    {
        var scenario = new SuppressionScenario()
            .WithFile(SuppressionScan.SlopwatchBaselinePath, $"{{ \"entries\": [ {{ \"ruleId\": \"SW003\", \"filePath\": \"{Source}\" }} ] }}");

        var exception = await Assert.That(async () => _ = await scenario.ScanAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("'hash'");
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
