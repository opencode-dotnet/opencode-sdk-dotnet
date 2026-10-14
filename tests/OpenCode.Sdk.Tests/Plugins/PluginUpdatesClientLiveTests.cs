using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Plugins;

/// <summary>
/// The update probes' inventory-only arms against a forced-owned server: no target (200), no
/// targets to update (204), and a target outside the inventory (400). The fixture's configuration
/// is owned and package-free by construction - an empty config seed over an isolated config root,
/// even when other tests attach to an external endpoint - and every test also proves its
/// inventory carries no package source before it calls, so none of these calls can reach a
/// registry.
/// </summary>
/// <remarks>
/// Deliberately not covered, because they reach the real npm registry, which no test may touch
/// (ADR-0022): <c>plugin.check</c>'s <c>outdated</c> flag needs a package-sourced plugin whose
/// version pacote resolves online (<c>packages/server/src/handlers/plugin.ts:29-46</c> with
/// <c>packages/util/src/npm.ts:374-375</c>, <c>preferOnline</c>, at the pin), and
/// <c>plugin.update</c>'s 503 <c>ServiceUnavailableError</c> (<c>handlers/plugin.ts:79</c>) needs
/// such a package's update to fail. The pin has no switch to stub that resolution, so those arms
/// are unreachable deterministically and are named here rather than skipped.
/// </remarks>
[ClassDataSource<OwnedPinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PluginUpdatesClientLiveTests(OwnedPinnedOpenCodeServerFixture server)
{
    private const string AbsentTarget = "@opencode-sdk-dotnet/live-absent-plugin";

    [Test]
    [Timeout(60_000)]
    public async Task CheckPluginUpdatesAsync_Should_Answer_The_Inventory_When_No_Target_Is_Named(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var listed = await GetActivatedInventoryAsync(client, cancellationToken);
        await Assert.That(IdsWhere(listed, static plugin => plugin.Source is PluginSourcePackage)).IsEmpty();

        var inventory = await client.Plugins.CheckPluginUpdatesAsync(cancellationToken: cancellationToken);

        await Assert.That(inventory.Status).IsEqualTo(200);
        await Assert.That(inventory.IsError).IsFalse();
        await Assert.That(inventory.Check.Count).IsGreaterThan(0);

        Console.WriteLine(
            "plugins-live: check status=" + Number(inventory.Status) +
            " count=" + Number(inventory.Check.Count) +
            " sources=" + string.Join(
                ", ", inventory.Check.Select(static plugin => plugin.Source.Type).Distinct(StringComparer.Ordinal)));
    }

    [Test]
    [Timeout(60_000)]
    public async Task CheckPluginUpdatesAsync_Should_Refuse_A_Target_Outside_The_Inventory(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var listed = await GetActivatedInventoryAsync(client, cancellationToken);
        await Assert.That(IdsWhere(listed, static plugin => plugin.Source is PluginSourcePackage)).IsEmpty();
        await Assert.That(IdsWhere(
            listed,
            static plugin => string.Equals(plugin.Id, AbsentTarget, StringComparison.Ordinal))).IsEmpty();

        var refused = await client.Plugins.CheckPluginUpdatesAsync(
            new PluginCheckRequest { Target = AbsentTarget }, OpenCodeRequestOptions.NoThrow, cancellationToken);

        await Assert.That(refused.Status).IsEqualTo(400);
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(refused.Error).IsTypeOf<InvalidRequestError>();
        var invalid = refused.Error as InvalidRequestError;
        await Assert.That(invalid?.Field).IsEqualTo("target");
        await Assert.That(invalid?.Message).Contains(AbsentTarget);

        Console.WriteLine(
            "plugins-live: check-unknown status=" + Number(refused.Status) +
            " field=" + invalid?.Field +
            " body=" + refused.RawBody);
    }

    [Test]
    [Timeout(60_000)]
    public async Task UpdatePluginsAsync_Should_Answer_204_When_No_Targets_Are_Named(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var listed = await GetActivatedInventoryAsync(client, cancellationToken);
        await Assert.That(IdsWhere(listed, static plugin => plugin.Source is PluginSourcePackage)).IsEmpty();

        var updated = await client.Plugins.UpdatePluginsAsync(
            new PluginUpdateRequest { Targets = [] }, cancellationToken: cancellationToken);

        await Assert.That(updated.Status).IsEqualTo(204);
        await Assert.That(updated.IsError).IsFalse();

        Console.WriteLine("plugins-live: update-empty status=" + Number(updated.Status));
    }

    [Test]
    [Timeout(60_000)]
    public async Task UpdatePluginsAsync_Should_Refuse_A_Target_Outside_The_Inventory(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var listed = await GetActivatedInventoryAsync(client, cancellationToken);
        await Assert.That(IdsWhere(listed, static plugin => plugin.Source is PluginSourcePackage)).IsEmpty();
        await Assert.That(IdsWhere(
            listed,
            static plugin => string.Equals(plugin.Id, AbsentTarget, StringComparison.Ordinal))).IsEmpty();

        var refused = await client.Plugins.UpdatePluginsAsync(
            new PluginUpdateRequest { Targets = [AbsentTarget] }, OpenCodeRequestOptions.NoThrow, cancellationToken);

        await Assert.That(refused.Status).IsEqualTo(400);
        await Assert.That(refused.IsError).IsTrue();
        await Assert.That(refused.Error).IsTypeOf<InvalidRequestError>();
        var invalid = refused.Error as InvalidRequestError;
        await Assert.That(invalid?.Field).IsEqualTo("targets");
        await Assert.That(invalid?.Message).Contains(AbsentTarget);

        Console.WriteLine(
            "plugins-live: update-unknown status=" + Number(refused.Status) +
            " field=" + invalid?.Field +
            " body=" + refused.RawBody);
    }

    /// <summary>The ids of the inventory entries a predicate selects; empty is the passing answer.</summary>
    private static string[] IdsWhere(IReadOnlyList<PluginInfo> plugins, Func<PluginInfo, bool> predicate) =>
        [.. plugins.Where(predicate).Select(static plugin => plugin.Id ?? "<null>")];

    private static async Task<IReadOnlyList<PluginInfo>> GetActivatedInventoryAsync(
        OpenCodeClient client, CancellationToken cancellationToken)
    {
        var listed = await LiveReadiness.PluginAsync(client, TestRpcPlugin.Id, cancellationToken);
        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        return listed.Plugins;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
