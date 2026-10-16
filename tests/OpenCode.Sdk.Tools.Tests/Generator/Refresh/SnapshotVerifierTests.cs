using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SnapshotVerifierTests
{
    [Test]
    public async Task ParseReceipt_Should_Refuse_A_Null_Patches_Member()
    {
        using var scenario = await RefreshScenario.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes(RefreshScenarioData.WithNullMember(RefreshScenarioData.Serialize(scenario.Receipt), "patches"));
        var exception = Assert.Throws<SnapshotRefreshException>(() => SnapshotStateReader.ParseReceipt(bytes, "spec/receipt.json"));

        await Assert.That(exception.Message).StartsWith("receipt 'spec/receipt.json' is invalid:");
        await Assert.That(exception.Message).Contains("patches");
    }

    [Test]
    [Arguments("hash", "accepted document hash")]
    [Arguments("digest", "operation-set digest")]
    [Arguments("operations", "operation count")]
    [Arguments("components", "component count")]
    [Arguments("content", "contentSchema count")]
    [Arguments("checkout", "submodule checkout")]
    [Arguments("patch", "patch manifest")]
    [Arguments("watch", "source watch")]
    [Arguments("observation", "cannot be read")]
    public async Task VerifyAsync_Should_Report_An_Isolated_Disagreement(string variation, string diagnostic)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        var receipt = RefreshScenarioData.WithStatistic(scenario.Receipt, variation);
        var state = scenario.State with { Receipt = Encoding.UTF8.GetBytes(RefreshScenarioData.Serialize(receipt)) };
        if (variation == "checkout")
        {
            state = state with { Checkout = new string('b', 40) };
        }

        if (variation == "patch")
        {
            _ = await scenario.AddPatchAsync("invalid.json", 1);
            await scenario.FileSystem.File.WriteAllTextAsync("spec/patches/invalid.json", "null");
        }
        if (variation == "watch")
        {
            state = state with { SourceWatch = "null"u8.ToArray() };
        }

        if (variation == "observation")
        {
            var watch = RefreshScenarioData.Watch(1, RefreshScenarioData.Watched("lost.ts", "anchor", "anchor"));
            state = state with { SourceWatch = Encoding.UTF8.GetBytes(RefreshScenarioData.Serialize(watch)) };
            scenario.Runner.Expect("git", ["show", $"{state.Checkout}:lost.ts"], scenario.FileSystem.Path.GetFullPath(SnapshotPaths.Submodule), ScriptedProcessRunner.Fail("missing"));
        }

        var result = await scenario.Services.GetRequiredService<SnapshotVerifier>().VerifyAsync(state, CancellationToken.None);
        await Assert.That(result.Problems.Single()).Contains(diagnostic);
    }

    [Test]
    [Arguments("null", "cannot be JSON null")]
    [Arguments("{", "is invalid")]
    public async Task VerifyAsync_Should_Throw_For_An_Unreadable_Receipt(string text, string diagnostic)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        var state = scenario.State with { Receipt = Encoding.UTF8.GetBytes(text) };
        var exception = await Assert.That(async () => _ = await scenario.Services.GetRequiredService<SnapshotVerifier>()
            .VerifyAsync(state, CancellationToken.None)).Throws<SnapshotRefreshException>();
        await Assert.That(exception!.Message).Contains(diagnostic);
    }

    [Test]
    [Arguments("added")]
    [Arguments("removed")]
    [Arguments("renamed")]
    [Arguments("hash")]
    [Arguments("reordered")]
    [Arguments("matching")]
    public async Task VerifyAsync_Should_Report_Patch_Set_Drift(string variation)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        var first = await scenario.AddPatchAsync("001-first.json", 1);
        var second = await scenario.AddPatchAsync("002-second.json", 2);
        IReadOnlyList<ReceiptPatch> recorded = variation switch
        {
            "added" => [first],
            "removed" => [first, second, second with { Manifest = "003-removed.json" }],
            "renamed" => [first with { Manifest = "old-name.json" }, second],
            "hash" => [first with { PatchSha256 = new string('a', 64) }, second],
            "reordered" => [second, first],
            _ => [first, second],
        };
        var state = scenario.State with { Receipt = Encoding.UTF8.GetBytes(RefreshScenarioData.Serialize(scenario.Receipt with { Patches = recorded })) };

        var outcome = await scenario.Services.GetRequiredService<SnapshotVerifier>().VerifyAsync(state, CancellationToken.None);

        if (variation == "matching")
        {
            await Assert.That(outcome.Problems).IsEmpty();
        }
        else
        {
            await Assert.That(outcome.Problems.Single()).Contains("ordered patch set");
        }
    }
}
