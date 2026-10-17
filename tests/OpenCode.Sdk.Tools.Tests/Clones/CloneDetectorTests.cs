using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Tools.Clones;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Clones;

/// <summary>How the detector runs one scope: the arguments each scope gets, the manifest split in its config, and the report it reads.</summary>
public sealed class CloneDetectorTests
{
    private const string Config = "work/scope.json";
    private const string GeneratedModel = "Models/Agent.cs";
    private const string HandWrittenClient = "src/OpenCode.Sdk/OpenCodeClient.cs";
    private const string Fingerprints = "--fail-on-new-clones";
    private const string FailOnEmpty = "--fail-on-empty";

    [Test]
    public async Task ReadSourceSplitAsync_Should_Refuse_A_Null_Files_Member()
    {
        var scenario = await new CloneScenario().BuildAsync();
        var json = JsonNode.Parse(await scenario.FileSystem.File.ReadAllTextAsync(CloneScenario.ManifestPath))!;
        json["files"] = null;
        await scenario.FileSystem.File.WriteAllTextAsync(CloneScenario.ManifestPath, json.ToJsonString());

        var detector = Detector(scenario);
        var exception = await Assert.That(async () => _ = await detector.ReadSourceSplitAsync(scenario.Root, CancellationToken.None))
            .Throws<CloneDetectorException>();

        await Assert.That(exception!.InnerException).IsTypeOf<JsonException>();
        await Assert.That(exception.InnerException!.Message).Contains("files");
        await Assert.That(exception.Message).Contains(".generated-manifest.json' is not a file list.");
    }

    [Test]
    public async Task RunAsync_Should_Write_The_Using_Directive_Ignore_Pattern()
    {
        var scenario = await new CloneScenario().WithRun("src").BuildAsync();
        _ = await RunAsync(scenario, "src");
        var config = scenario.FileSystem.Path.Combine(scenario.FileSystem.Path.GetTempPath(), "detector-work", "src.json");
        using var document = JsonDocument.Parse(await scenario.FileSystem.File.ReadAllTextAsync(config));

        await Assert.That(document.RootElement.TryGetProperty("ignorePattern", out var patterns)).IsTrue();
        await Assert.That(patterns.EnumerateArray().Select(static pattern => pattern.GetString()!))
            .IsEquivalentTo([@"(?m)^[ \t]*(global[ \t]+)?using[ \t]+(static[ \t]+)?([A-Za-z_][A-Za-z0-9_]*[ \t]*=[ \t]*)?[A-Za-z_][A-Za-z0-9_.]*[ \t]*;"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunAsync_Should_Ignore_A_Shared_Using_Header()
    {
        var result = await new CloneScenario().DetectUsingPairAsync(resourceBody: false);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Clones).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_Should_Keep_A_Shared_Body_With_Using_Statements()
    {
        var result = await new CloneScenario().DetectUsingPairAsync(resourceBody: true);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Clones).IsEqualTo(1);
        await Assert.That(result.DuplicatedLines).IsGreaterThan(10);
    }

    [Test]
    public async Task RunAsync_Should_Keep_String_Literal_Using_Text_In_Body_Clone_Tokens()
    {
        var tokens = await new CloneScenario().DetectStringLiteralPairTokensAsync();

        await Assert.That(tokens[0]).IsGreaterThan(50);
        await Assert.That(tokens[0]).IsEqualTo(tokens[1]);
    }

    [Test]
    public async Task Arguments_Should_Gate_Hand_Written_Src_Against_Its_Baseline()
    {
        var arguments = CloneDetector.Arguments(Scope("src"), Config, updateBaseline: false);

        await Assert.That(arguments).IsEquivalentTo(
            [CloneDetector.RunnerPath, "src", "--config", Config, FailOnEmpty, "--baseline", "tools/jscpd/src.baseline.json", Fingerprints], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Arguments_Should_Add_The_Three_Percent_Threshold_For_Tools()
    {
        var arguments = CloneDetector.Arguments(Scope("tools"), Config, updateBaseline: false);

        await Assert.That(arguments).IsEquivalentTo(
            [CloneDetector.RunnerPath, "tools", "--config", Config, FailOnEmpty, "--baseline", "tools/jscpd/tools.baseline.json", Fingerprints, "--threshold", "3"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Arguments_Should_Gate_Tests_Against_Its_Baseline_And_The_Threshold()
    {
        var arguments = CloneDetector.Arguments(Scope("tests"), Config, updateBaseline: false);

        await Assert.That(arguments).IsEquivalentTo(
            [CloneDetector.RunnerPath, "tests", "--config", Config, FailOnEmpty, "--baseline", "tools/jscpd/tests.baseline.json", Fingerprints, "--threshold", "6"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("generated", "src")]
    public async Task Arguments_Should_Run_A_Report_Scope_Without_Gate_Arguments(string name, string path)
    {
        var arguments = CloneDetector.Arguments(Scope(name), Config, updateBaseline: true);

        await Assert.That(arguments).IsEquivalentTo([CloneDetector.RunnerPath, path, "--config", Config, FailOnEmpty], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Arguments_Should_Swap_In_The_Baseline_Update_For_A_Gated_Scope()
    {
        var arguments = CloneDetector.Arguments(Scope("tools"), Config, updateBaseline: true);

        await Assert.That(arguments).IsEquivalentTo(
            [CloneDetector.RunnerPath, "tools", "--config", Config, FailOnEmpty, "--baseline", "tools/jscpd/tools.baseline.json", "--update-baseline"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunAsync_Should_Leave_Every_Manifest_File_Out_Of_The_Src_Scope()
    {
        var ignore = await IgnoredByAsync("src");

        await Assert.That(ignore).Contains("src/OpenCode.Sdk/" + GeneratedModel);
        await Assert.That(ignore).DoesNotContain(HandWrittenClient);
        await Assert.That(ignore).Contains("**/bin/**");
    }

    [Test]
    public async Task RunAsync_Should_Leave_Every_Hand_Written_File_Out_Of_The_Generated_Scope()
    {
        var ignore = await IgnoredByAsync("generated");

        await Assert.That(ignore).Contains(HandWrittenClient);
        await Assert.That(ignore).DoesNotContain("src/OpenCode.Sdk/" + GeneratedModel);
    }

    [Test]
    public async Task RunAsync_Should_Read_The_Totals_Of_The_Report()
    {
        var scenario = await new CloneScenario().WithRun("src", clones: 4, newClones: 1).BuildAsync();

        var result = await RunAsync(scenario, "src");

        await Assert.That(result.Clones).IsEqualTo(4);
        await Assert.That(result.DuplicatedLines).IsEqualTo(40);
        await Assert.That(result.Lines).IsEqualTo(1000);
        await Assert.That(result.Percentage).IsEqualTo(4.0);
        await Assert.That(result.NewClones).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_Should_Place_Each_New_Clone_Under_The_Scanned_Path_And_Leave_Old_Ones_Out()
    {
        var scenario = await new CloneScenario().WithRun("src", exitCode: 1, clones: 5, newClones: 1).BuildAsync();

        var result = await RunAsync(scenario, "src");

        // Only membership and cardinality matter; this expected collection has one item.
        await Assert.That(result.NewCloneLocations).IsEquivalentTo([new CloneLocation("src/NestedFolderPastTheWrapColumn/A0.cs:3-33", "src/NestedFolderPastTheWrapColumn/B0.cs:5-35", 212)], CollectionOrdering.Any);
    }

    [Test]
    public async Task RunAsync_Should_Refuse_When_The_Detector_Writes_No_Report()
    {
        var scenario = await new CloneScenario().WithRun("tests", exitCode: 2, standardError: "jscpd: no prebuilt binary", writesReport: false).BuildAsync();

        var exception = await Assert.That(async () => _ = await RunAsync(scenario, "tests")).Throws<CloneDetectorException>();

        await Assert.That(exception!.Message).Contains("'tests'").And.Contains("no prebuilt binary");
    }

    [Test]
    public async Task ReadSourceSplitAsync_Should_Refuse_A_Missing_Manifest()
    {
        var scenario = await new CloneScenario().WithoutManifest().BuildAsync();

        var exception = await Assert.That(async () => _ = await Detector(scenario).ReadSourceSplitAsync(scenario.Root, CancellationToken.None))
            .Throws<CloneDetectorException>();

        await Assert.That(exception!.Message).Contains(".generated-manifest.json");
    }

    [Test]
    public async Task EnsureInstalled_Should_Refuse_With_The_Install_Command_When_The_Detector_Is_Missing()
    {
        var scenario = await new CloneScenario().WithoutDetector().BuildAsync();

        var exception = await Assert.That(() => Detector(scenario).EnsureInstalled(scenario.Root)).Throws<CloneDetectorException>();

        await Assert.That(exception!.Message).Contains("bun install --frozen-lockfile --ignore-scripts");
    }

    private static CloneScope Scope(string name) => CloneScopes.All.Single(scope => scope.Name == name);

    private static CloneDetector Detector(CloneScenario scenario) => new(scenario.FileSystem, scenario.Runner);

    private static async Task<CloneScopeResult> RunAsync(CloneScenario scenario, string scope)
    {
        var detector = Detector(scenario);
        var split = await detector.ReadSourceSplitAsync(scenario.Root, CancellationToken.None);
        var work = scenario.FileSystem.Path.Combine(scenario.FileSystem.Path.GetTempPath(), "detector-work");
        scenario.FileSystem.Directory.CreateDirectory(work);
        return await detector.RunAsync(scenario.Root, Scope(scope), split, work, updateBaseline: false, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<string>> IgnoredByAsync(string scope)
    {
        var scenario = await new CloneScenario()
            .WithGenerated(GeneratedModel)
            .WithHandWritten(HandWrittenClient)
            .WithRun(scope)
            .BuildAsync();
        _ = await RunAsync(scenario, scope);

        var config = scenario.FileSystem.Path.Combine(scenario.FileSystem.Path.GetTempPath(), "detector-work", scope + ".json");
        using var document = JsonDocument.Parse(await scenario.FileSystem.File.ReadAllTextAsync(config));
        return [.. document.RootElement.GetProperty("ignore").EnumerateArray().Select(static glob => glob.GetString()!)];
    }
}
