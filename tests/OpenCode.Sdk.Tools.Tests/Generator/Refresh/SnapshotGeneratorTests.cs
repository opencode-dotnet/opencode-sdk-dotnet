using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Infrastructure.Logging;
using OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SnapshotGeneratorTests
{
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
