using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Events;

/// <summary>
/// The rpc family's live event proof against the pinned server: the repository-owned plugin's
/// <c>emit</c> method publishes a declared event, and the SDK's event bus types it as
/// <see cref="EventRpc"/> with the exact prefixed type, the caller's nonce, and the plugin's
/// registration location. The subscription is attached (<see cref="EventServerConnected"/> seen)
/// before the emit call, so the proof rests on a causal barrier rather than on replay.
/// This proof needs the owned server; an external endpoint has no owned plugin and fails fast.
/// </summary>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class EventsClientLiveTests(PinnedOpenCodeServerFixture server)
{
    /// <summary>Upstream forms the type as <c>rpc.&lt;definition id&gt;.&lt;event name&gt;</c> (<c>core/src/rpc.ts</c>).</summary>
    private const string ObservedEventType = "rpc." + TestRpcPlugin.Id + ".observed";

    private const string EmitMethod = "emit";

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);

    [Test]
    [Timeout(180_000)]
    public async Task SubscribeAsync_Should_Deliver_The_Owned_Rpc_Event_With_Its_Nonce_And_Location(
        CancellationToken cancellationToken)
    {
        var ownedPlugin = server.RpcPlugin ??
            throw new InvalidOperationException(
                "The rpc event proof requires the owned pinned server; an external endpoint has no owned plugin.");
        using var client = server.CreateClient();
        await AssertOwnedPluginActiveAsync(client, ownedPlugin, cancellationToken);
        var resolved = (await client.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation;

        var nonce = "rpc-event-live-" + Guid.NewGuid().ToString("N");
        var reader = new OwnedEventReader(EventWait, CleanupTimeout, cancellationToken);
        var probe = new SessionEventProbe(reader);
        Exception? primaryFailure = null;

        try
        {
            // The subscription is attached before the emit call, so the proof rests on a causal
            // barrier rather than on replay.
            probe.Start(client.Events.SubscribeAsync(cancellationToken: reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);

            var emitted = await client.Rpc.CallAsync(
                TestRpcPlugin.Id,
                EmitMethod,
                new RpcCallRequestBuilder().WithNonce(nonce).Build(),
                cancellationToken: cancellationToken);
            await Assert.That(emitted.Status).IsEqualTo(200);
            await Assert.That(emitted.IsError).IsFalse();
            var output = emitted.Call.Output ?? throw new InvalidOperationException("The emit response had no output.");
            await Assert.That(output.GetProperty("nonce").GetString()).IsEqualTo(nonce);

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var observed = await probe.WaitForAsync<EventRpc>(
                rpc => string.Equals(rpc.Type, ObservedEventType, StringComparison.Ordinal)
                    && rpc.Data.TryGetValue("nonce", out var carried)
                    && carried.ValueKind == JsonValueKind.String
                    && string.Equals(carried.GetString(), nonce, StringComparison.Ordinal),
                "the rpc event '" + ObservedEventType + "' carrying the nonce", barrier.Token);
            await Assert.That(observed.Data.Count).IsEqualTo(1);
            await Assert.That(observed.Location.Directory).IsEqualTo(resolved.Directory);

            Console.WriteLine(
                "rpc-event-live: mode=owned arm=emit status=" + Number(emitted.Status) +
                " type=" + observed.Type +
                " nonce=" + nonce +
                " directory=" + observed.Location.Directory);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (!exception.Data.Contains(EventDiagnosticSummary.DataKey))
            {
                exception.Data[EventDiagnosticSummary.DataKey] = probe.DiagnosticSummary;
            }
        }
        finally
        {
            await reader.CompleteAsync(primaryFailure);
        }
    }

    /// <summary>
    /// Activation is awaited in the client's location before the event proof depends on the plugin,
    /// and the exact owned entry must be the active local file the fixture seeded.
    /// </summary>
    private static async Task AssertOwnedPluginActiveAsync(
        OpenCodeClient client,
        TestRpcPlugin ownedPlugin,
        CancellationToken cancellationToken)
    {
        var listed = await LiveReadiness.PluginAsync(client, TestRpcPlugin.Id, cancellationToken);
        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        var ownedEntries = listed.Plugins.Where(
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
