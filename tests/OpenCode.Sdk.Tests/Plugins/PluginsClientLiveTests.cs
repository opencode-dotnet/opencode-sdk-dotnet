using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Plugins;

/// <summary>
/// The plugin inventory against the pinned server. <c>plugin.list</c> reads the inventory as it
/// stands (<c>packages/server/src/handlers/plugin.ts:11-13</c> at the pin) and the pin exposes no
/// HTTP activation barrier, so the test waits for the entry its branch can expect before it
/// asserts: the seeded repository-owned RPC plugin on an owned server, any entry at all on an
/// external endpoint. Both branches assert - an owned server carries its known builtins plus the
/// seeded local plugin, and an external endpoint cannot carry that plugin at all.
/// </summary>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PluginsClientLiveTests(PinnedOpenCodeServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ListPluginsAsync_Should_Report_The_Expected_Inventory(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var listed = await ReadReadyInventoryAsync(client, cancellationToken);

        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();

        await Assert.That(listed.Plugins.Count).IsGreaterThan(0);

        // The plugin, not the ownership, is what the inventory shape follows: every owned server
        // carries the seeded entry, whichever build the fixture started, and an external endpoint
        // carries none - which is also the proof that this lane really attached elsewhere.
        if (server.RpcPlugin is { } ownedPlugin)
        {
            await AssertOwnedInventoryAsync(listed.Plugins, ownedPlugin);
        }
        else
        {
            await Assert.That(server.IsExternal).IsTrue();
            await Assert.That(IdsWhere(
                listed.Plugins,
                static plugin => string.Equals(plugin.Id, TestRpcPlugin.Id, StringComparison.Ordinal))).IsEmpty();
        }

        Console.WriteLine(
            "plugins-live: mode=" + (server.IsExternal ? "external" : "owned") +
            " list status=" + Number(listed.Status) +
            " count=" + Number(listed.Plugins.Count) +
            " ids=" + string.Join(", ", listed.Plugins.Select(static plugin => plugin.Id ?? "<null>")));
    }

    /// <summary>The ids of the inventory entries a predicate selects; empty is the passing answer.</summary>
    private static string[] IdsWhere(IReadOnlyList<PluginInfo> plugins, Func<PluginInfo, bool> predicate) =>
        [.. plugins.Where(predicate).Select(static plugin => plugin.Id ?? "<null>")];

    private Task<PluginListResponse> ReadReadyInventoryAsync(OpenCodeClient client, CancellationToken cancellationToken) =>
        server.RpcPlugin is null
            ? LiveReadiness.WaitAsync(
                token => client.Plugins.ListPluginsAsync(cancellationToken: token),
                static listed => listed.Plugins.Count > 0,
                "a non-empty plugin inventory", cancellationToken)
            : LiveReadiness.PluginAsync(client, TestRpcPlugin.Id, cancellationToken);

    private static async Task AssertOwnedInventoryAsync(
        IReadOnlyList<PluginInfo> plugins,
        TestRpcPlugin ownedPlugin)
    {
        await Assert.That(plugins.Select(static plugin => plugin.Id).ToArray()).Contains("opencode.agent");
        await Assert.That(IdsWhere(
            plugins,
            static plugin => plugin.Source is not PluginSourceBuiltin &&
                !string.Equals(plugin.Id, TestRpcPlugin.Id, StringComparison.Ordinal))).IsEmpty();

        var ownedEntries = plugins.Where(
            static plugin => string.Equals(plugin.Id, TestRpcPlugin.Id, StringComparison.Ordinal)).ToArray();
        await Assert.That(ownedEntries).Count().IsEqualTo(1);
        var owned = ownedEntries.Single();
        await Assert.That(owned.Source).IsTypeOf<PluginSourceLocal>();
        var local = owned.Source as PluginSourceLocal;
        await Assert.That(local?.Path).IsEqualTo(ownedPlugin.EntryPoint);
        await Assert.That(owned.State).IsTypeOf<PluginStateActive>();
    }

    /// <summary>Renders one number for the console line, culture-free.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
