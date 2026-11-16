using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Infrastructure.Logging;
using OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SnapshotGeneratorTests
{
    [Test]
    [Arguments("all", "all", "upstream's generator already satisfies patch '001-test.json' (component 'Prompt.FileRef' carries a schema); retire the patch with an empty-patch refresh")]
    [Arguments("partial", "all", "upstream's generator satisfies patch '001-test.json' for only some components (component 'Prompt.FileRef' carries a schema, component 'Prompt.Other' lacks it); the patch needs human review")]
    [Arguments("none", "partial", "patch '001-test.json' did not repair component 'Prompt.Other' (it still lacks a schema after the patched generator run); the patch needs human review")]
    [Arguments("none", "all", null)]
    [Arguments("boolean", "all", null)]
    public async Task GenerateAsync_Should_Apply_ComponentAbsent_Repair_Rules(string presence, string repaired, string? diagnostic)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        _ = await scenario.AddPatchAsync("001-test.json", 1);
        var fs = scenario.FileSystem;
        var manifest = RefreshScenarioData.Manifest(sha256: DocumentInspector.Sha256Hex(RefreshScenario.Document)) with
        {
            RepairPredicate = new PatchPredicate { Type = "componentAbsent", Components = ["Prompt.FileRef", "Prompt.Other"] },
        };
        await fs.File.WriteAllTextAsync("spec/patches/001-test.json", RefreshScenarioData.Serialize(manifest));
        var patches = await scenario.Services.GetRequiredService<PatchSetLoader>().LoadAsync(CancellationToken.None);
        var worktree = fs.Path.GetFullPath(".scratchpad/generation/worktree");
        var submodule = fs.Path.GetFullPath(SnapshotPaths.Submodule);
        var protocol = fs.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage);
        var artifact = fs.Path.Combine(worktree, SnapshotPaths.UpstreamArtifact);
        scenario.Runner.Expect("git", ["-c", "core.longpaths=true", "worktree", "add", "--detach", worktree, RefreshScenarioData.Commit], submodule);
        scenario.Runner.Expect("bun", ["install", "--frozen-lockfile", "--ignore-scripts"], worktree, sideEffect: () =>
        {
            _ = fs.Directory.CreateDirectory(protocol);
            return Task.CompletedTask;
        });
        scenario.Runner.Expect("bun", ["run", "generate"], protocol,
            sideEffect: () => fs.File.WriteAllBytesAsync(artifact, RefreshScenarioData.ComponentPresenceDocument(presence)));
        if (presence is "none" or "boolean")
        {
            scenario.Runner.Expect("git", ["show", $"{RefreshScenarioData.Commit}:{manifest.Touches[0]}"], submodule, ScriptedProcessRunner.Ok("before"));
            scenario.Runner.Expect("git", ["-c", "core.longpaths=true", "apply", fs.Path.GetFullPath("spec/patches/001-test.patch")], worktree);
            scenario.Runner.Expect("bun", ["run", "generate"], protocol,
                sideEffect: () => fs.File.WriteAllBytesAsync(artifact, RefreshScenarioData.ComponentPresenceDocument(repaired)));
        }
        scenario.Runner.Expect("git", ["-c", "core.longpaths=true", "worktree", "remove", "--force", worktree], submodule);
        var generator = scenario.Services.GetRequiredService<SnapshotGenerator>();
        if (diagnostic is null)
        {
            var result = await generator.GenerateAsync(RefreshScenarioData.Commit, ".scratchpad/generation", patches, CancellationToken.None);
            await Assert.That(System.Text.Json.Nodes.JsonNode.Parse(result.NormalizedBytes)!["components"]!["schemas"]!.AsObject()
                .Select(static schema => schema.Key)).IsEquivalentTo(["Prompt.FileRef", "Prompt.Other"], CollectionOrdering.Matching);
        }
        else
        {
            var exception = await Assert.That(async () => _ = await generator.GenerateAsync(
                RefreshScenarioData.Commit, ".scratchpad/generation", patches, CancellationToken.None)).Throws<SnapshotRefreshException>();
            await Assert.That(exception!.Message).IsEqualTo(diagnostic);
        }
    }

    [Test]
    [Arguments("success", false)]
    [Arguments("primary failure", false)]
    [Arguments("stale", false)]
    [Arguments("success", true)]
    [Arguments("primary failure", true)]
    [Arguments("cancellation", true)]
    public async Task GenerateAsync_Should_Warn_Without_Replacing_The_Primary_Outcome(string outcome, bool cleanupThrows)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var fs = scenario.FileSystem;
        scenario.Services.GetRequiredService<ToolLoggingOptions>().Apply(LogLevel.Trace, ".scratchpad/cleanup.log");
        await fs.File.WriteAllTextAsync(".scratchpad/cleanup.log", string.Empty);
        var worktree = fs.Path.GetFullPath(fs.Path.Combine(".scratchpad", "generation", "worktree"));
        var submodule = fs.Path.GetFullPath(SnapshotPaths.Submodule);
        if (outcome == "stale")
        {
            _ = fs.Directory.CreateDirectory(worktree);
        }
        else
        {
            scenario.Runner.Expect("git", ["-c", "core.longpaths=true", "worktree", "add", "--detach", worktree, RefreshScenarioData.Commit], submodule, sideEffect: () =>
            {
                _ = fs.Directory.CreateDirectory(fs.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage));
                return Task.CompletedTask;
            });
            scenario.Runner.Expect("bun", ["install", "--frozen-lockfile", "--ignore-scripts"], worktree,
                outcome == "primary failure" ? ScriptedProcessRunner.Fail("primary failure") : ScriptedProcessRunner.Ok(), sideEffect: async () =>
                {
                    if (outcome != "cancellation")
                    {
                        return;
                    }

                    await cancellation.CancelAsync();
                    cancellation.Token.ThrowIfCancellationRequested();
                });
            if (outcome == "success")
            {
                var artifact = fs.Path.Combine(worktree, SnapshotPaths.UpstreamArtifact);
                scenario.Runner.Expect("bun", ["run", "generate"], fs.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage),
                    sideEffect: () => fs.File.WriteAllBytesAsync(artifact, RefreshScenario.Document));
            }
        }

        scenario.Runner.Expect("git", ["-c", "core.longpaths=true", "worktree", "remove", "--force", worktree], submodule, ScriptedProcessRunner.Fail("cleanup [failed]"),
            sideEffect: cleanupThrows ? () => throw new InvalidOperationException("cleanup [failed]") : null);
        var generator = scenario.Services.GetRequiredService<SnapshotGenerator>();
        if (outcome == "success")
        {
            var generated = await generator.GenerateAsync(RefreshScenarioData.Commit, ".scratchpad/generation", [], CancellationToken.None);
            await Assert.That(generated.NormalizedBytes).IsEquivalentTo(RefreshScenario.Document, CollectionOrdering.Matching);
        }
        else if (outcome == "cancellation")
        {
            var exception = await Assert.That(async () => _ = await generator.GenerateAsync(
                RefreshScenarioData.Commit, ".scratchpad/generation", [], cancellation.Token)).Throws<OperationCanceledException>();
            await Assert.That(exception!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            var exception = await Assert.That(async () => _ = await generator.GenerateAsync(
                RefreshScenarioData.Commit, ".scratchpad/generation", [], CancellationToken.None)).Throws<SnapshotRefreshException>();
            await Assert.That(exception!.Message).Contains(outcome == "stale" ? "stale worktree" : "installing upstream packages");
        }

        var warning = await fs.File.ReadAllTextAsync(".scratchpad/cleanup.log");
        await Assert.That(warning).StartsWith("Warning:").And.Contains(worktree)
            .And.Contains(cleanupThrows ? "InvalidOperationException" : "exit 1").And.Contains("cleanup [failed]");
    }

    [Test]
    public async Task GenerateAsync_Should_Record_Identical_Preimages_With_Autocrlf_Enabled()
    {
        using var scenario = new AutocrlfRefreshScenario();
        var lf = await scenario.GenerateAsync(autocrlf: false);
        var lfCheckout = scenario.CheckoutBytes;
        var crlf = await scenario.GenerateAsync(autocrlf: true);

        await Assert.That(System.Text.Encoding.UTF8.GetString(lfCheckout)).DoesNotContain("\r\n");
        await Assert.That(System.Text.Encoding.UTF8.GetString(scenario.CheckoutBytes)).Contains("\r\n");
        await Assert.That(scenario.CheckoutBytes).IsNotEquivalentTo(lfCheckout, CollectionOrdering.Matching);
        await Assert.That(crlf.Patches.Single().Preimages).IsEquivalentTo(lf.Patches.Single().Preimages, CollectionOrdering.Matching);
        await Assert.That(crlf.Patches.Single().Preimages[0].Sha256).IsEqualTo(DocumentInspector.Sha256Hex(scenario.BlobBytes));
    }
}
