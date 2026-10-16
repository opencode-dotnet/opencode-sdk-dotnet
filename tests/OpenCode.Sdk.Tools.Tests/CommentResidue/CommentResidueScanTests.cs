using OpenCode.Sdk.Tools.Tests.CommentResidue.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.CommentResidue;

/// <summary>Each residue rule, in each comment form the gate reads, and what it leaves alone.</summary>
public sealed class CommentResidueScanTests
{
    private const string Source = "src/A.cs";
    private const string Script = "tests/Fixtures/peer.js";
    private const string Workflow = ".github/workflows/ci.yml";
    private const string Type = "internal sealed class A;";
    private const string TypeHeader = "internal sealed class A";
    private const string GuidePath = "docs/guide/a.md";
    private const string TaskFive = "Task 5";
    private const string Inactive = "#if NEVER_DEFINED";
    private const string EndInactive = "#endif";

    [Test]
    public async Task Scan_Should_Read_Only_The_Supplied_Nonignored_File_Set()
    {
        var hits = await new CommentResidueScenario()
            .WithFile("src/bin/A.cs", "// Task 5")
            .WithIgnoredFile("ignored.cs", "// Q157")
            .ScanAsync();

        await Assert.That(hits).Contains($"src/bin/A.cs:1: {CommentResidueScan.TaskNumber}: '{TaskFive}'");
        await Assert.That(hits).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Scan_Should_Ignore_A_Manifest_Outside_The_Supplied_File_Set()
    {
        var hits = await new CommentResidueScenario().WithFile(Source, "// Task 5")
            .WithIgnoredFile(GeneratedManifest.ManifestPath, "not a manifest").ScanAsync();

        await Assert.That(hits).Contains($"{Source}:1: {CommentResidueScan.TaskNumber}: '{TaskFive}'");
        await Assert.That(hits).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Scan_Should_Report_A_Task_Number_In_A_Line_Comment()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(Source, "// The guard Task 5 added.", Type)
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo([$"{Source}:1: {CommentResidueScan.TaskNumber}: '{TaskFive}'"]);
    }

    [Test]
    public async Task Scan_Should_Report_A_Design_Section_On_The_Line_Of_A_Documentation_Comment()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(Source, "/// <summary>", "/// The fixture (design §7.2).", "/// </summary>", Type)
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo([$"{Source}:2: {CommentResidueScan.DesignSection}: 'design §'"]);
    }

    [Test]
    public async Task Scan_Should_Read_An_Inactive_Region_As_CSharp()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(
                Source,
                Inactive,
                "// research log Q157",
                "/* the guard Task 5 added */",
                "internal static class B { public const string Text = @\"// Q158\"; public static void M() => VerifierSettings.AutoVerify(); }",
                EndInactive,
                Type)
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo(
        [
            $"{Source}:2: {CommentResidueScan.ResearchLogNumber}: 'Q157'",
            $"{Source}:3: {CommentResidueScan.TaskNumber}: '{TaskFive}'",
            $"{Source}:4: {CommentResidueScan.SnapshotAutoAccept}: 'AutoVerify'",
        ]);
    }

    [Test]
    public async Task Scan_Should_Leave_The_Same_Text_In_A_String_Alone()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(Source, TypeHeader, "{", $"    public const string Text = \"// Task 5, design §7.2, Q157, {GuidePath}\";", "}")
            .ScanAsync();

        await Assert.That(hits).IsEmpty();
    }

    [Test]
    public async Task Scan_Should_Report_A_Documentation_Path_In_A_Project_File_Comment()
    {
        var hits = await new CommentResidueScenario()
            .WithFile("src/A.csproj", "<Project>", $"  <!-- See {GuidePath}. -->", "</Project>")
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo([$"src/A.csproj:2: {CommentResidueScan.DocumentationPath}: '{GuidePath}'"]);
    }

    [Test]
    public async Task Scan_Should_Report_A_Workflow_Comment_But_Not_A_Hash_Inside_A_Quoted_Value()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(Workflow, "jobs:", "  build: # measured in Q172", "    name: \"build # Q173\"", "    url: https://example.test/#Q174")
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo([$"{Workflow}:2: {CommentResidueScan.ResearchLogNumber}: 'Q172'"]);
    }

    [Test]
    public async Task Scan_Should_Report_A_PowerShell_Block_Comment()
    {
        var hits = await new CommentResidueScenario()
            .WithFile("tools/setup.ps1", "<#", "  The guard Task 5 added.", "#>", "Write-Output 'done'")
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo([$"tools/setup.ps1:2: {CommentResidueScan.TaskNumber}: '{TaskFive}'"]);
    }

    [Test]
    public async Task Scan_Should_Report_The_Comments_Of_A_Script_And_Skip_Its_Strings_And_Regular_Expressions()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(
                Script,
                "const text = \"// Task 5\";",
                "const block = '/* Q157 */';",
                "const pattern = /[//]Task 5/;",
                "switch (x) { case 1:// Q158",
                "}",
                "/*",
                $" * Mirrors {GuidePath}.",
                " */")
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo(
        [
            $"{Script}:4: {CommentResidueScan.ResearchLogNumber}: 'Q158'",
            $"{Script}:7: {CommentResidueScan.DocumentationPath}: '{GuidePath}'",
        ]);
    }

    [Test]
    public async Task Scan_Should_Report_A_Comment_In_The_Banned_Symbols_File()
    {
        var hits = await new CommentResidueScenario()
            .WithFile("BannedSymbols.txt", "# Rules from Q157.", "P:System.DateTime.Now;Use UtcNow")
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo([$"BannedSymbols.txt:1: {CommentResidueScan.ResearchLogNumber}: 'Q157'"]);
    }

    [Test]
    public async Task Scan_Should_Leave_Manifest_Listed_Generated_Files_And_Markdown_Alone()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(GeneratedManifest.ManifestPath, "{ \"files\": [ \"Models/B.cs\" ] }")
            .WithFile(GeneratedManifest.SdkPath + "Models/B.cs", "// Generated by OpenCode.Sdk.Tools from spec/openapi.json.", "// Task 5", "internal sealed class B;")
            .WithFile("docs/guide/README.md", "Task 5, design §7.2")
            .ScanAsync();

        await Assert.That(hits).IsEmpty();
    }

    [Test]
    public async Task Scan_Should_Read_A_Hand_Written_File_That_Claims_The_Generated_Header()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(GeneratedManifest.ManifestPath, "{ \"files\": [ ] }")
            .WithFile(Source, "// Generated by OpenCode.Sdk.Tools, it says.", "// Task 5", Type)
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo([$"{Source}:2: {CommentResidueScan.TaskNumber}: '{TaskFive}'"]);
    }

    [Test]
    public async Task Scan_Should_Report_A_Snapshot_Auto_Accept_In_Code_And_In_Configuration()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(Source, "internal static class A", "{", "    public static void Initialize() => VerifierSettings.AutoVerify();", "    public static bool CanAutoVerifySignature;", "}")
            .WithFile("tests/settings.json", "{", "  \"autoVerify\": true", "}")
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo(
        [
            $"{Source}:3: {CommentResidueScan.SnapshotAutoAccept}: 'AutoVerify'",
            $"tests/settings.json:2: {CommentResidueScan.SnapshotAutoAccept}: '\"autoVerify\": true'",
        ]);
    }

    [Test]
    public async Task Scan_Should_Report_A_Stryker_Suppression_Only_Without_A_Reason()
    {
        var hits = await new CommentResidueScenario()
            .WithFile(
                Source,
                "// Stryker disable once Equality",
                "// Stryker disableall",
                "/* Stryker disable all : */",
                "// Stryker disable once Boolean : the flag is a constant",
                "/* Stryker disable all : a generated block */",
                Type)
            .ScanAsync();

        await Assert.That(hits).IsEquivalentTo(
        [
            $"{Source}:1: {CommentResidueScan.UnexplainedStrykerDisable}: '// Stryker disable once Equality'",
            $"{Source}:2: {CommentResidueScan.UnexplainedStrykerDisable}: '// Stryker disableall'",
            $"{Source}:3: {CommentResidueScan.UnexplainedStrykerDisable}: '/* Stryker disable all : */'",
        ]);
    }
}
