using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;
using OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SnapshotSynchronizerTests
{
    private static readonly byte[] AcceptedDocument = RefreshScenarioData.DocumentBytes(spec => spec
        .WithOperation("v2.alpha.get", path: "/api/alpha")
        .WithOperation("v2.beta.get", path: "/api/beta"));

    private static readonly byte[] CandidateDocument = RefreshScenarioData.DocumentBytes(spec => spec
        .WithOperation("v2.beta.get", path: "/api/beta")
        .WithOperation("v2.gamma.get", path: "/api/gamma"));

    private static readonly byte[] LacksStreamLink = RefreshScenarioData.DocumentBytes(spec => spec
        .WithOperation("v2.beta.get", path: "/api/beta")
        .WithSchema("V2EventEncoded", schema => schema.Type("string")));

    private static readonly byte[] CarriesStreamLink = RefreshScenarioData.DocumentBytes(spec => spec
        .WithOperation("v2.beta.get", path: "/api/beta")
        .WithSchema("Plain", schema => schema.Type("string"))
        .WithSchema("V2EventEncoded", schema => schema
            .Type("string")
            .ContentSchema("application/json", payload => payload.Ref("Plain"))));

    private static readonly byte[] LacksParameterStyle = ParameterDocument(styled: false);
    private static readonly byte[] CarriesParameterStyle = ParameterDocument(styled: true);

    private static readonly string[] RepairComponents = ["V2EventEncoded", "Plain", "Missing"];
    private static readonly string[] RepairOperations = ["experimental.artifacts.list", "experimental.artifacts.get", "experimental.artifacts.delete"];

    [Test]
    [Arguments("cl", "upstream's generator satisfies patch '001-test.json' for only some components (component 'V2EventEncoded' carries 'contentSchema', component 'Plain' lacks it); the patch needs human review")]
    [Arguments("lc", "upstream's generator satisfies patch '001-test.json' for only some components (component 'Plain' carries 'contentSchema', component 'V2EventEncoded' lacks it); the patch needs human review")]
    [Arguments("ca", "component 'Plain' named by patch '001-test.json' is absent from the generated baseline; the patch needs human review")]
    [Arguments("cla", "component 'Missing' named by patch '001-test.json' is absent from the generated baseline; the patch needs human review")]
    [Arguments("cc", "upstream's generator already satisfies patch '001-test.json' (component 'V2EventEncoded' carries 'contentSchema'); retire the patch with an empty-patch refresh")]
    public async Task Prepare_Should_Review_Or_Retire_A_Multi_Component_Patch(string presence, string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(presence);
        using var scenario = await MultiTargetScenarioAsync(parameters: false, presence);

        var exception = await scenario.PrepareRefusalAsync();

        await Assert.That(exception.Message).IsEqualTo(diagnostic);
    }

    [Test]
    [Arguments("cl", "upstream's generator satisfies patch '001-test.json' for only some targets (operation 'experimental.artifacts.list' parameter 'location' carries style 'deepObject', operation 'experimental.artifacts.get' parameter 'location' lacks it); the patch needs human review")]
    [Arguments("lc", "upstream's generator satisfies patch '001-test.json' for only some targets (operation 'experimental.artifacts.get' parameter 'location' carries style 'deepObject', operation 'experimental.artifacts.list' parameter 'location' lacks it); the patch needs human review")]
    [Arguments("ca", "operation 'experimental.artifacts.get' parameter 'location' named by patch '001-test.json' is absent from the generated baseline; the patch needs human review")]
    [Arguments("cla", "operation 'experimental.artifacts.delete' parameter 'location' named by patch '001-test.json' is absent from the generated baseline; the patch needs human review")]
    [Arguments("cc", "upstream's generator already satisfies patch '001-test.json' (operation 'experimental.artifacts.list' parameter 'location' carries style 'deepObject'); retire the patch with an empty-patch refresh")]
    public async Task Prepare_Should_Review_Or_Retire_A_Multi_Parameter_Patch(string presence, string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(presence);
        using var scenario = await MultiTargetScenarioAsync(parameters: true, presence);

        var exception = await scenario.PrepareRefusalAsync();

        await Assert.That(exception.Message).IsEqualTo(diagnostic);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Prepare_Should_Keep_A_Multi_Target_Patch_When_Every_Target_Lacks_The_Repair(bool parameters)
    {
        using var scenario = await MultiTargetScenarioAsync(parameters, "ll");

        var outcome = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None);

        await Assert.That(outcome.Receipt.Patches.Select(static patch => patch.Manifest))
            .IsEquivalentTo(["001-test.json"], CollectionOrdering.Matching);
        await Assert.That(await scenario.FileSystem.File.ReadAllBytesAsync(outcome.NormalizedDocumentPath, CancellationToken.None))
            .IsEquivalentTo(MultiTargetDocument(parameters, "cc"), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("satisfied", "upstream's generator already satisfies patch '001-test.json' (operation 'experimental.artifacts.list' parameter 'location' carries style 'deepObject'); retire the patch with an empty-patch refresh")]
    [Arguments("absent", "operation 'experimental.artifacts.list' parameter 'location' named by patch '001-test.json' is absent from the generated baseline; the patch needs human review")]
    [Arguments("unrepaired", "patch '001-test.json' did not repair operation 'experimental.artifacts.list' parameter 'location' (it still lacks style 'deepObject' after the patched generator run); the patch needs human review")]
    public async Task Prepare_Should_Refuse_An_Invalid_Parameter_Style_Repair(string variation, string diagnostic)
    {
        var fileSystem = await CreateRepositoryAsync();
        await WriteParameterPatchSetAsync(fileSystem);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(LacksParameterStyle);
        var baseline = variation switch
        {
            "satisfied" => CarriesParameterStyle,
            "absent" => CandidateDocument,
            _ => LacksParameterStyle,
        };
        ExpectGeneration(runner, fileSystem, baseline, patched: variation == "unrepaired" ? LacksParameterStyle : null);

        var exception = await scenario.PrepareRefusalAsync();

        await Assert.That(exception.Message).IsEqualTo(diagnostic);
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Accept_A_Patch_That_Adds_The_Parameter_Style()
    {
        var fileSystem = await CreateRepositoryAsync();
        await WriteParameterPatchSetAsync(fileSystem);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(LacksParameterStyle);
        ExpectGeneration(runner, fileSystem, LacksParameterStyle, patched: CarriesParameterStyle);

        PrepareOutcome? outcome = null;
        await Assert.That(async () => outcome = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None)).ThrowsNothing();

        // A successful PrepareAsync returns its non-null outcome.
        await Assert.That(outcome!.Receipt.Patches.Single().Manifest).IsEqualTo("001-test.json");
        await Assert.That(await fileSystem.File.ReadAllBytesAsync(outcome.NormalizedDocumentPath, CancellationToken.None))
            .IsEquivalentTo(CarriesParameterStyle, CollectionOrdering.Matching);
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Run_The_Pinned_Generator_Without_Patches()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(CandidateDocument);
        ExpectGeneration(runner, fileSystem, CandidateDocument);

        var outcome = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None);

        var receipt = outcome.Receipt;
        await Assert.That(receipt.UpstreamCommit).IsEqualTo(RefreshScenarioData.Commit);
        // Only membership and cardinality matter; this expected collection has one item.
        await Assert.That(receipt.AddedOperations).IsEquivalentTo(["v2.gamma.get"], CollectionOrdering.Any);
        // Only membership and cardinality matter; this expected collection has one item.
        await Assert.That(receipt.RemovedOperations).IsEquivalentTo(["v2.alpha.get"], CollectionOrdering.Any);
        await Assert.That(receipt.Patches).IsEmpty();
        await Assert.That(receipt.GeneratedBaselineSha256).IsEqualTo(DocumentInspector.Sha256Hex(CandidateDocument));
        await Assert.That(receipt.NormalizedDocumentSha256).IsEqualTo(DocumentInspector.Sha256Hex(CandidateDocument));
        await Assert
            .That(await fileSystem.File.ReadAllBytesAsync(outcome.NormalizedDocumentPath, CancellationToken.None))
            .IsEquivalentTo(CandidateDocument, CollectionOrdering.Matching);
        await Assert.That(outcome.ReceiptPath).Satisfies(fileSystem.File.Exists);
        await Assert
            .That(runner.Invocations.Count(static invocation => invocation.StartsWith("bun run generate", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Install_The_Generator_Output_Over_A_Stale_Committed_Artifact()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(AcceptedDocument);
        ExpectGeneration(runner, fileSystem, CandidateDocument);

        var outcome = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None);

        var receipt = outcome.Receipt;
        await Assert.That(receipt.RawDocumentSha256).IsEqualTo(DocumentInspector.Sha256Hex(AcceptedDocument));
        await Assert.That(receipt.GeneratedBaselineSha256).IsEqualTo(DocumentInspector.Sha256Hex(CandidateDocument));
        await Assert.That(receipt.NormalizedDocumentSha256).IsEqualTo(DocumentInspector.Sha256Hex(CandidateDocument));
        // Only membership and cardinality matter; this expected collection has one item.
        await Assert.That(receipt.AddedOperations).IsEquivalentTo(["v2.gamma.get"], CollectionOrdering.Any);
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Refuse_A_Repair_Predicate_The_Generator_Satisfies()
    {
        var fileSystem = await CreateRepositoryAsync();
        await WritePatchSetAsync(fileSystem);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(LacksStreamLink);
        ExpectGeneration(runner, fileSystem, CarriesStreamLink);

        var exception = await scenario.PrepareRefusalAsync();

        await Assert.That(exception.Message).Contains("retire the patch");
        await Assert.That(runner.Invocations).DoesNotContain(static invocation => invocation.StartsWith("git -c core.longpaths=true apply", StringComparison.Ordinal));
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Keep_A_Patch_A_Stale_Committed_Artifact_Appears_To_Satisfy()
    {
        var fileSystem = await CreateRepositoryAsync();
        await WritePatchSetAsync(fileSystem);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(CarriesStreamLink);
        ExpectGeneration(runner, fileSystem, LacksStreamLink, patched: CarriesStreamLink);
        var outcome = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None);

        await Assert.That(outcome.Receipt.Patches.Count).IsEqualTo(1);
        await Assert.That(outcome.Receipt.GeneratedBaselineSha256).IsEqualTo(DocumentInspector.Sha256Hex(LacksStreamLink));
        await Assert.That(outcome.Receipt.NormalizedDocumentSha256).IsEqualTo(DocumentInspector.Sha256Hex(CarriesStreamLink));
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Run_The_Pinned_Generator_Over_Patches()
    {
        var baseline = LacksStreamLink;
        var normalized = CarriesStreamLink;
        var fileSystem = await CreateRepositoryAsync();
        await WritePatchSetAsync(fileSystem);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(baseline);
        ExpectGeneration(runner, fileSystem, baseline, patched: normalized);
        var outcome = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None);

        var receipt = outcome.Receipt;
        await Assert.That(receipt.GeneratedBaselineSha256).IsEqualTo(DocumentInspector.Sha256Hex(baseline));
        await Assert.That(receipt.NormalizedDocumentSha256).IsEqualTo(DocumentInspector.Sha256Hex(normalized));
        var preimage = receipt.Patches.Single().Preimages.Single();
        await Assert.That(preimage.Path).IsEqualTo("packages/protocol/script/generate-openapi.ts");
        await Assert.That(preimage.Sha256).IsEqualTo(DocumentInspector.Sha256Hex(Encoding.UTF8.GetBytes("before")));
        await Assert
            .That(await fileSystem.File.ReadAllBytesAsync(outcome.NormalizedDocumentPath, CancellationToken.None))
            .IsEquivalentTo(normalized, CollectionOrdering.Matching);
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    [Arguments(false, "did not repair component 'V2EventEncoded'")]
    [Arguments(true, "patch '001-test.json' touches 'packages/protocol/script/generate-openapi.ts', which does not exist at the target commit")]
    public async Task Prepare_Should_Refuse_A_Patch_That_Does_Not_Repair_Its_Component(bool missingPreimage, string diagnostic)
    {
        var fileSystem = await CreateRepositoryAsync();
        await WritePatchSetAsync(fileSystem);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(LacksStreamLink);
        ExpectGeneration(runner, fileSystem, LacksStreamLink, patched: LacksStreamLink, missingPreimage: missingPreimage);

        var exception = await scenario.PrepareRefusalAsync();

        await Assert.That(exception.Message).Contains(diagnostic);
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Refuse_A_Repair_Component_The_Baseline_Does_Not_Declare()
    {
        var fileSystem = await CreateRepositoryAsync();
        await WritePatchSetAsync(fileSystem);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(LacksStreamLink);
        ExpectGeneration(runner, fileSystem, CandidateDocument);

        var exception = await scenario.PrepareRefusalAsync();

        await Assert.That(exception.Message).Contains("is absent from the generated baseline");
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Prepare_Should_Refuse_A_Generator_Run_That_Writes_No_Document()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(CandidateDocument);
        ExpectGeneration(runner, fileSystem, baseline: null, staleArtifact: CandidateDocument);

        var exception = await scenario.PrepareRefusalAsync();

        await Assert.That(exception.Message).Contains("exited cleanly but wrote no");
        await Assert.That(runner.Invocations[^1]).StartsWith("git -c core.longpaths=true worktree remove");
    }

    [Test]
    public async Task Apply_Should_Refuse_A_Receipt_Without_A_Generator_Baseline()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem, CreateMutationRunner(fileSystem));
        var receipt = CreateReceipt(CandidateDocument, "scratch/openapi.json");
        await scenario.StageCandidateAsync(receipt, CandidateDocument);
        await fileSystem.File.WriteAllTextAsync("scratch/receipt.json",
            RefreshScenarioData.WithNullMember(RefreshScenarioData.Serialize(receipt), "generatedBaselineSha256"));
        var exception = await scenario.ApplyRefusalAsync();

        await Assert.That(exception.Message).Contains("is invalid:");
        await Assert.That(exception.Message).Contains("generatedBaselineSha256");
        await Assert.That(await fileSystem.File.ReadAllBytesAsync(SnapshotPaths.AcceptedDocument, CancellationToken.None))
            .IsEquivalentTo(AcceptedDocument, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Apply_Should_Refuse_Time_Of_Check_Drift()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem, CreateMutationRunner(fileSystem));
        var receipt = CreateReceipt(CandidateDocument, "scratch/openapi.json");
        await scenario.StageCandidateAsync(receipt, AcceptedDocument);
        var exception = await scenario.ApplyRefusalAsync();

        await Assert.That(exception.Message).Contains("re-run prepare");
    }

    [Test]
    public async Task Apply_Should_Install_The_Accepted_Snapshot_Paths()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        var receipt = CreateReceipt(CandidateDocument, "scratch/openapi.json");
        await scenario.StageCandidateAsync(receipt, CandidateDocument);
        var runner = scenario.ExpectApply();
        var applied = await scenario.Synchronizer.ApplyAsync("scratch/receipt.json", CancellationToken.None);

        await Assert.That(applied.NormalizedDocumentPath).IsNull();
        await Assert
            .That(await fileSystem.File.ReadAllBytesAsync(SnapshotPaths.AcceptedDocument, CancellationToken.None))
            .IsEquivalentTo(CandidateDocument, CollectionOrdering.Matching);
        var committed = JsonSerializer.Deserialize(
            await fileSystem.File.ReadAllTextAsync(SnapshotPaths.CommittedReceipt, CancellationToken.None),
            ToolJsonContext.Default.SnapshotReceipt);
        await Assert.That(committed!.NormalizedDocumentPath).IsNull();
        var snapshotMarkdown = await fileSystem.File.ReadAllTextAsync(SnapshotPaths.SnapshotMarkdown, CancellationToken.None);
        await Assert.That(snapshotMarkdown).Contains($"| Commit | `{RefreshScenarioData.Commit}` |");
        await Assert.That(snapshotMarkdown).DoesNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await Assert.That(snapshotMarkdown).DoesNotContain("Date: 2026-08-13");
        await Assert.That(runner.Invocations).Contains(invocation =>
            invocation.StartsWith($"git -c core.longpaths=true checkout --detach {RefreshScenarioData.Commit}", StringComparison.Ordinal));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Verify_Should_Report_A_Missing_Receipt(bool unreadableHead)
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem, new ScriptedProcessRunner()
            .Expect("git", ["rev-parse", "HEAD"], Submodule(fileSystem), unreadableHead
                ? ScriptedProcessRunner.Fail("uninitialized submodule") : ScriptedProcessRunner.Ok(RefreshScenarioData.Commit)));

        var outcome = await scenario.Synchronizer.VerifyAsync(CancellationToken.None);

        await Assert.That(outcome.IsReproduced).IsFalse();
        await Assert.That(outcome.Problems.Single()).Contains("no committed receipt");
    }

    [Test]
    public async Task Verify_Should_Reproduce_A_Matching_State()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        await scenario.SeedAcceptedReceiptAsync(CreateReceipt(CandidateDocument, normalizedDocumentPath: null), CandidateDocument);
        _ = scenario.Runner
            .Expect("git", ["rev-parse", "HEAD"], Submodule(fileSystem), ScriptedProcessRunner.Ok(RefreshScenarioData.Commit + "\n"));

        var outcome = await scenario.Synchronizer.VerifyAsync(CancellationToken.None);

        await Assert.That(outcome.Problems).IsEmpty();
        await Assert.That(outcome.IsReproduced).IsTrue();
    }

    [Test]
    public async Task Verify_Should_Report_Document_And_Checkout_Mismatches()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        await scenario.SeedAcceptedReceiptAsync(CreateReceipt(CandidateDocument, normalizedDocumentPath: null));
        _ = scenario.Runner
            .Expect("git", ["rev-parse", "HEAD"], Submodule(fileSystem), ScriptedProcessRunner.Ok(new string('b', 40) + "\n"));

        var outcome = await scenario.Synchronizer.VerifyAsync(CancellationToken.None);

        await Assert.That(outcome.Problems).Contains(static problem => problem.Contains("hash", StringComparison.Ordinal));
        await Assert.That(outcome.Problems).Contains(static problem => problem.Contains("checkout", StringComparison.Ordinal));
    }

    [Test]
    public async Task Prepare_Should_Record_Every_Watched_Source_In_The_Receipt()
    {
        const string handler = "closeAccepted(new Socket.CloseEvent(4404, \"session not found\"))";
        var fileSystem = await CreateRepositoryAsync();
        var pinned = RefreshScenarioData.Watched("packages/server/src/handlers/pty.ts", handler, "4404");
        await WriteSourceWatchAsync(fileSystem, pinned);
        using var scenario = new RefreshScenario(fileSystem);
        var runner = scenario.ExpectReference(CandidateDocument)
            .Expect("git", ["show", $"{RefreshScenarioData.Commit}:{pinned.Path}"], Submodule(fileSystem), ScriptedProcessRunner.Ok(handler));
        ExpectGeneration(runner, fileSystem, CandidateDocument);
        var outcome = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None);

        var watched = outcome.Receipt.WatchedSources.Single();
        await Assert.That(watched.Path).IsEqualTo(pinned.Path);
        await Assert.That(watched.Sha256).IsEqualTo(pinned.Sha256);
        await Assert.That(watched.AnchorMatched).IsTrue();
    }

    [Test]
    public async Task Prepare_Should_Refuse_A_Watched_Source_The_Candidate_Lost()
    {
        var fileSystem = await CreateRepositoryAsync();
        var pinned = RefreshScenarioData.Watched("packages/core/src/pty/ticket.ts", "Duration.seconds(60)", "seconds(60)");
        await WriteSourceWatchAsync(fileSystem, pinned);
        using var scenario = new RefreshScenario(fileSystem);
        _ = scenario.ExpectReference(CandidateDocument)
            .Expect("git", ["show", $"{RefreshScenarioData.Commit}:{pinned.Path}"], Submodule(fileSystem),
                ScriptedProcessRunner.Fail("fatal: path does not exist"));

        var exception = await Assert
            .That(async () => _ = await scenario.Synchronizer.PrepareAsync("origin/v2", CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("watched source 'packages/core/src/pty/ticket.ts' cannot be read");
    }

    [Test]
    public async Task Verify_Should_Report_A_Watched_Source_That_Moved()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        var pinned = RefreshScenarioData.PtyBufferSource;
        await scenario.SeedWatchedReceiptAsync(CreateReceipt(CandidateDocument, null), pinned, anchorMatched: true, CandidateDocument);
        _ = scenario.ExpectObservedSource(pinned, "const BUFFER_LIMIT = 4 * 1024 * 1024");

        var outcome = await scenario.Synchronizer.VerifyAsync(CancellationToken.None);

        // A checkout that moved under the door disagrees with both readings: the committed pin
        // and the accepted receipt's own record of the same file.
        await Assert.That(outcome.Problems).Contains(static problem =>
            problem.Contains("watched source 'packages/core/src/pty.ts' changed: pinned", StringComparison.Ordinal));
        await Assert.That(outcome.Problems).Contains(static problem =>
            problem.Contains("the receipt records watched source 'packages/core/src/pty.ts' at", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verify reproduces the receipt's watchedSources section rather than trusting it: here the
    /// checkout matches the committed pin exactly, and the only disagreement is the receipt's own
    /// anchorMatched record (ADR-0020).
    /// </summary>
    [Test]
    public async Task Verify_Should_Report_A_Receipt_Whose_Watched_Sources_It_Cannot_Reproduce()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        var pinned = RefreshScenarioData.PtyBufferSource;
        await scenario.SeedWatchedReceiptAsync(CreateReceipt(CandidateDocument, null), pinned, anchorMatched: false, CandidateDocument);
        _ = scenario.ExpectObservedSource(pinned, "const BUFFER_LIMIT = 1024 * 1024 * 2");

        var outcome = await scenario.Synchronizer.VerifyAsync(CancellationToken.None);

        await Assert.That(outcome.Problems.Single()).Contains("anchorMatched=false, but this checkout observes true");
    }

    [Test]
    public async Task Apply_Should_Repin_The_Watch_Over_The_Reviewed_Receipt()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem);
        var pinned = RefreshScenarioData.PtyBufferSource;
        await WriteSourceWatchAsync(fileSystem, pinned);
        var moved = RefreshScenarioData.Watched(pinned.Path, "const BUFFER_LIMIT = 1024 * 1024 * 4", "BUFFER_LIMIT");
        var receipt = CreateReceipt(CandidateDocument, "scratch/openapi.json") with
        {
            WatchedSources = [Observation(moved.Path, moved.Sha256, anchorMatched: true)],
        };
        await scenario.StageCandidateAsync(receipt, CandidateDocument);
        _ = scenario.ExpectApply();

        _ = await scenario.Synchronizer.ApplyAsync("scratch/receipt.json", CancellationToken.None);

        var repinned = await new SourceWatchLoader(fileSystem).LoadAsync(CancellationToken.None);
        await Assert.That(repinned.Sources.Single().Sha256).IsEqualTo(moved.Sha256);
        await Assert.That(repinned.Sources.Single().Behavior).IsEqualTo(pinned.Behavior);
    }

    [Test]
    public async Task Apply_Should_Refuse_A_Receipt_Whose_Anchor_Was_Lost()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem, CreateMutationRunner(fileSystem));
        var pinned = RefreshScenarioData.PtyBufferSource;
        await WriteSourceWatchAsync(fileSystem, pinned);
        var receipt = CreateReceipt(CandidateDocument, "scratch/openapi.json") with
        {
            WatchedSources = [Observation(pinned.Path, new string('d', 64), anchorMatched: false)],
        };
        await scenario.StageCandidateAsync(receipt, CandidateDocument);
        var exception = await scenario.ApplyRefusalAsync();

        await Assert.That(exception.Message).Contains("lost anchors in packages/core/src/pty.ts");
    }

    [Test]
    public async Task Apply_Should_Leave_All_Accepted_Artifacts_Unchanged_When_The_Last_Check_Fails()
    {
        var fileSystem = await CreateRepositoryAsync();
        using var scenario = new RefreshScenario(fileSystem, CreateMutationRunner(fileSystem));
        var pinned = RefreshScenarioData.Watched("tracked.ts", "old checkout", "checkout");
        await WriteSourceWatchAsync(fileSystem, pinned);
        var receipt = CreateReceipt(CandidateDocument, "scratch/openapi.json") with
        {
            WatchedSources = [Observation(pinned.Path, pinned.Sha256, anchorMatched: true)],
        };
        await scenario.StageCandidateAsync(receipt, CandidateDocument);
        await fileSystem.File.WriteAllTextAsync(SnapshotPaths.CommittedReceipt, RefreshScenarioData.Serialize(CreateReceipt(AcceptedDocument, null)));
        await fileSystem.File.WriteAllTextAsync(SnapshotPaths.SnapshotMarkdown, "invalid managed identity");
        var head = new string('a', 40);
        var tracked = fileSystem.Path.Combine(SnapshotPaths.Submodule, pinned.Path);
        await fileSystem.File.WriteAllTextAsync(tracked, "old checkout");
        var paths = new[] { SnapshotPaths.AcceptedDocument, SnapshotPaths.CommittedReceipt, SnapshotPaths.SnapshotMarkdown, SnapshotPaths.SourceWatch, tracked };
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            before.Add(path, await fileSystem.File.ReadAllBytesAsync(path));
        }
        var runner = scenario.Runner
            .Expect("git", ["rev-parse", "--verify", $"{RefreshScenarioData.Commit}^{{commit}}"], Submodule(fileSystem))
            .Expect("git", ["-c", "core.longpaths=true", "checkout", "--detach", RefreshScenarioData.Commit], Submodule(fileSystem), sideEffect: async () =>
            {
                head = RefreshScenarioData.Commit;
                await fileSystem.File.WriteAllTextAsync(tracked, "new checkout");
            });

        await Assert.That(async () => _ = await scenario.Synchronizer.ApplyAsync("scratch/receipt.json", CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        foreach (var path in paths)
        {
            await Assert.That(await fileSystem.File.ReadAllBytesAsync(path)).IsEquivalentTo(before[path], CollectionOrdering.Matching);
        }

        await Assert.That(head).IsEqualTo(new string('a', 40));
        await Assert.That(runner.Invocations.Where(static invocation => invocation.StartsWith("git -c core.longpaths=true checkout", StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task Prepare_Should_Record_The_Named_Release_Tag()
    {
        var fileSystem = await CreateRepositoryAsync();
        var runner = CreateMutationRunner(fileSystem)
            .Expect("git", ["rev-parse", "--symbolic-full-name", "v2.0.30"], Submodule(fileSystem), ScriptedProcessRunner.Ok("refs/tags/v2.0.30"))
            .Expect("git", ["rev-parse", "v2.0.30^{commit}"], Submodule(fileSystem), ScriptedProcessRunner.Ok(RefreshScenarioData.Commit))
            .Expect("git", ["show", $"{RefreshScenarioData.Commit}:{SnapshotPaths.UpstreamArtifact}"], Submodule(fileSystem), ScriptedProcessRunner.Ok(CandidateDocument));
        ExpectGeneration(runner, fileSystem, CandidateDocument);
        using var scenario = new RefreshScenario(fileSystem, runner);

        var outcome = await scenario.Synchronizer.PrepareAsync("v2.0.30", CancellationToken.None);

        using var document = JsonDocument.Parse(await fileSystem.File.ReadAllBytesAsync(outcome.ReceiptPath));
        await Assert.That(document.RootElement.TryGetProperty("releaseTag", out var tag)).IsTrue();
        await Assert.That(tag.GetString()).IsEqualTo("v2.0.30");
    }

    [Test]
    [Arguments("refs/tags/v2.0.30", "refs/tags/v2.0.30", "v2.0.30")]
    [Arguments("annotated", "refs/tags/annotated", "annotated")]
    [Arguments("main", "refs/heads/main", null)]
    [Arguments(RefreshScenarioData.Commit, "", null)]
    public async Task Prepare_Should_Record_Tag_Provenance_Only_For_Tag_References(string reference, string symbolic, string? tag)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        _ = scenario.ExpectReference(RefreshScenario.Document, reference, symbolic);
        scenario.ExpectUnpatchedGeneration();

        var outcome = await scenario.Synchronizer.PrepareAsync(reference, CancellationToken.None);

        await Assert.That(outcome.Receipt.UpstreamCommit).IsEqualTo(RefreshScenarioData.Commit);
        await Assert.That(outcome.Receipt.ReleaseTag).IsEqualTo(tag);
        using var document = JsonDocument.Parse(await scenario.FileSystem.File.ReadAllBytesAsync(outcome.ReceiptPath));
        await Assert.That(document.RootElement.GetProperty("releaseTag").GetString()).IsEqualTo(tag);
    }

    [Test]
    [Arguments("missing", "does not exist")]
    [Arguments("null", "cannot be JSON null")]
    [Arguments("malformed", "is invalid:")]
    [Arguments("fetch", "fetching upstream failed (git fetch origin, exit 1): offline")]
    [Arguments("checkout", "moving the submodule checkout failed (git -c core.longpaths=true checkout --detach 0123456789abcdef0123456789abcdef01234567, exit 1): offline")]
    [Arguments("head", "reading the submodule checkout failed (git rev-parse HEAD, exit 1): offline")]
    public async Task Apply_Should_Refuse_Unreadable_Receipts_Or_A_Failed_Fetch(string variation, string diagnostic)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        var fs = scenario.FileSystem;
        var before = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in new[] { SnapshotPaths.AcceptedDocument, SnapshotPaths.CommittedReceipt, SnapshotPaths.SnapshotMarkdown })
        {
            before.Add(path, await fs.File.ReadAllBytesAsync(path));
        }
        fs.File.Delete(RefreshScenario.ReceiptPath);
        if (variation != "missing")
        {
            await fs.File.WriteAllTextAsync(RefreshScenario.ReceiptPath, variation switch
            {
                "null" => "null",
                "malformed" => "{",
                _ => RefreshScenarioData.Serialize(scenario.Receipt),
            });
        }
        scenario.Runner.Expect("git", ["fetch", "origin"], Submodule(fs),
            variation == "fetch" ? ScriptedProcessRunner.Fail("offline") : ScriptedProcessRunner.Ok());
        scenario.Runner.Expect("git", ["rev-parse", "HEAD"], Submodule(fs),
            variation == "head" ? ScriptedProcessRunner.Fail("offline") : ScriptedProcessRunner.Ok(RefreshScenarioData.Commit));
        scenario.ExpectCommit();
        scenario.Runner.Expect("git", ["-c", "core.longpaths=true", "checkout", "--detach", RefreshScenarioData.Commit], Submodule(fs), ScriptedProcessRunner.Fail("offline"));

        var exception = await Assert.That(async () => _ = await scenario.Synchronizer.ApplyAsync(RefreshScenario.ReceiptPath, CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains(diagnostic);
        foreach (var (path, bytes) in before)
        {
            await Assert.That(await fs.File.ReadAllBytesAsync(path)).IsEquivalentTo(bytes, CollectionOrdering.Matching);
        }
        await Assert.That(fs.Directory.EnumerateFiles("spec", "source-watch.json")).IsEmpty();
    }

    private static ReceiptWatchedSource Observation(string path, string sha256, bool anchorMatched) =>
        new()
        {
            Path = path,
            Sha256 = sha256,
            AnchorMatched = anchorMatched,
        };

    private static async Task<MockFileSystem> CreateRepositoryAsync()
    {
        var fileSystem = new MockFileSystem();
        _ = fileSystem.Directory.CreateDirectory("spec");
        await fileSystem.File.WriteAllBytesAsync(SnapshotPaths.AcceptedDocument, AcceptedDocument);
        await fileSystem.File.WriteAllTextAsync(SnapshotPaths.SnapshotMarkdown, RefreshScenarioData.SnapshotMarkdown);
        _ = fileSystem.Directory.CreateDirectory(SnapshotPaths.Submodule);
        return fileSystem;
    }

    private static async Task<RefreshScenario> MultiTargetScenarioAsync(bool parameters, string presence)
    {
        var fileSystem = await CreateRepositoryAsync();
        if (parameters)
        {
            await WriteParameterPatchSetAsync(fileSystem);
        }
        else
        {
            await WritePatchSetAsync(fileSystem);
        }

        var path = fileSystem.Path.Combine(SnapshotPaths.PatchesRoot, "001-test.json");
        // The existing patch writers serialize a non-null typed manifest.
        var manifest = JsonSerializer.Deserialize(await fileSystem.File.ReadAllTextAsync(path), ToolJsonContext.Default.PatchManifest)!;
        var predicate = manifest.RepairPredicate with
        {
            Components = parameters ? null : RepairComponents[..presence.Length],
            Operations = parameters ? RepairOperations[..presence.Length] : null,
        };
        await fileSystem.File.WriteAllTextAsync(path, RefreshScenarioData.Serialize(manifest with { RepairPredicate = predicate }));
        var scenario = new RefreshScenario(fileSystem);
        var baseline = MultiTargetDocument(parameters, presence);
        var runner = scenario.ExpectReference(baseline);
        ExpectGeneration(runner, fileSystem, baseline, patched: presence == "ll" ? MultiTargetDocument(parameters, "cc") : null);
        return scenario;
    }

    private static byte[] MultiTargetDocument(bool parameters, string presence) => RefreshScenarioData.DocumentBytes(spec =>
    {
        for (var index = 0; index < presence.Length; index++)
        {
            var state = presence[index];
            if (state == 'a')
            {
                continue;
            }

            if (parameters)
            {
                spec.WithOperation(RepairOperations[index], path: "/" + RepairOperations[index], configure: operation => operation
                    .Parameter("location", "query", schema => schema.Type("object"), deepObject: state == 'c'));
            }
            else
            {
                spec.WithSchema(RepairComponents[index], schema =>
                {
                    schema.Type("string");
                    if (state == 'c')
                    {
                        schema.ContentSchema("application/json", payload => payload.Type("string"));
                    }
                });
            }
        }
    });

    private static byte[] ParameterDocument(bool styled) => RefreshScenarioData.DocumentBytes(spec => spec
        .WithOperation("experimental.artifacts.list", configure: operation => operation
            .Parameter("location", "query", schema => schema.Type("object"), deepObject: styled), path: "/artifacts"));

    private static async Task WriteParameterPatchSetAsync(MockFileSystem fileSystem)
    {
        await WritePatchSetAsync(fileSystem);
        var path = fileSystem.Path.Combine(SnapshotPaths.PatchesRoot, "001-test.json");
        // WritePatchSetAsync serializes a non-null typed manifest.
        var manifest = JsonNode.Parse(await fileSystem.File.ReadAllTextAsync(path))!;
        manifest["repairPredicate"] = new JsonObject
        {
            ["type"] = "parameterLacksStyle",
            ["operations"] = new JsonArray("experimental.artifacts.list"),
            ["parameter"] = "location",
            ["style"] = "deepObject",
        };
        await fileSystem.File.WriteAllTextAsync(path, manifest.ToJsonString());
    }

    private static async Task WritePatchSetAsync(MockFileSystem fileSystem)
    {
        const string patchContent = "diff --git a/x b/x";
        _ = fileSystem.Directory.CreateDirectory(SnapshotPaths.PatchesRoot);
        await fileSystem.File.WriteAllTextAsync(
            fileSystem.Path.Combine(SnapshotPaths.PatchesRoot, "001-test.json"),
            RefreshScenarioData.Serialize(
                RefreshScenarioData.Manifest(sha256: DocumentInspector.Sha256Hex(Encoding.UTF8.GetBytes(patchContent)))));
        await fileSystem.File.WriteAllTextAsync(fileSystem.Path.Combine(SnapshotPaths.PatchesRoot, "001-test.patch"), patchContent);
    }

    /// <summary>
    /// Scripts the generation worktree every prepare runs: the pinned generator's baseline (null for a
    /// run that writes nothing) and, when a patched document is given, the applied patch and the
    /// patched generator run.
    /// </summary>
    private static void ExpectGeneration(ScriptedProcessRunner runner, MockFileSystem fileSystem, byte[]? baseline,
        byte[]? patched = null, byte[]? staleArtifact = null, bool missingPreimage = false)
    {
        var worktree = fileSystem.Path.GetFullPath(
            fileSystem.Path.Combine(SnapshotPaths.ScratchRoot, RefreshScenarioData.Commit, "worktree"));
        var touched = fileSystem.Path.Combine(worktree, "packages/protocol/script/generate-openapi.ts");
        var artifact = fileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamArtifact);
        _ = runner
            .Expect("git", ["-c", "core.longpaths=true", "worktree", "add", "--detach", worktree, RefreshScenarioData.Commit], Submodule(fileSystem), sideEffect: async () =>
            {
                _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(touched)!);
                await fileSystem.File.WriteAllTextAsync(touched, "before");
                if (staleArtifact is not null)
                {
                    await fileSystem.File.WriteAllBytesAsync(artifact, staleArtifact);
                }
            })
            .Expect("bun", ["install", "--frozen-lockfile", "--ignore-scripts"], worktree, sideEffect: () =>
            {
                _ = fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(artifact)!);
                return Task.CompletedTask;
            })
            .Expect("bun", ["run", "generate"], fileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage), sideEffect: () =>
                baseline is null ? Task.CompletedTask : fileSystem.File.WriteAllBytesAsync(artifact, baseline));
        if (patched is not null)
        {
            _ = runner.Expect("git", ["show", $"{RefreshScenarioData.Commit}:packages/protocol/script/generate-openapi.ts"], Submodule(fileSystem),
                missingPreimage ? ScriptedProcessRunner.Fail("missing blob") : ScriptedProcessRunner.Ok("before"));
            if (!missingPreimage)
            {
                _ = runner.Expect("git", ["-c", "core.longpaths=true", "apply", fileSystem.Path.GetFullPath(fileSystem.Path.Combine(SnapshotPaths.PatchesRoot, "001-test.patch"))], worktree)
                    .Expect("bun", ["run", "generate"], fileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage), sideEffect: () => fileSystem.File.WriteAllBytesAsync(artifact, patched));
            }
        }

        _ = runner.Expect("git", ["-c", "core.longpaths=true", "worktree", "remove", "--force", worktree], Submodule(fileSystem));
    }

    private static string Submodule(MockFileSystem fileSystem) => fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);

    private static ScriptedProcessRunner CreateMutationRunner(MockFileSystem fileSystem) => new ScriptedProcessRunner()
        .Expect("git", ["fetch", "origin"], Submodule(fileSystem))
        .Expect("git", ["rev-parse", "HEAD"], Submodule(fileSystem), ScriptedProcessRunner.Ok(RefreshScenarioData.Commit));

    private static async Task WriteSourceWatchAsync(MockFileSystem fileSystem, params WatchedSource[] sources) =>
        await fileSystem.File.WriteAllTextAsync(
            SnapshotPaths.SourceWatch, RefreshScenarioData.Serialize(RefreshScenarioData.Watch(1, sources)));

    private static SnapshotReceipt CreateReceipt(byte[] normalizedBytes, string? normalizedDocumentPath)
        => RefreshScenarioData.Receipt(normalizedBytes, normalizedDocumentPath);
}
