using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Transport;

/// <summary>
/// The pinned server's half of the owned transport's idle-timeout parity. Upstream's client lets an
/// idle connection wait for reuse as long as the server's <c>Keep-Alive</c> hint minus a 2-second
/// threshold (undici's <c>keepAliveTimeoutThreshold</c>), a per-response rule the .NET handlers have
/// no hook for, so <see cref="TransportPolicy.ConnectionIdleMilliseconds"/> fixes the result for
/// this server's hint. A pin refresh that changes the hint fails here, naming the value to
/// re-derive, instead of drifting silently.
/// </summary>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class TransportParityLiveTests(PinnedOpenCodeServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task Pinned_Server_Should_Advertise_The_Keep_Alive_Hint_The_Idle_Timeout_Derives_From(CancellationToken cancellationToken)
    {
        using var http = TransportPolicy.CreateOwnedHttpClient(server.Endpoint);

        // Any answer carries the hint, an unauthenticated refusal included.
        using var response = await http.GetAsync(new Uri(server.Endpoint, "/api/info"), cancellationToken);
        var hint = response.Headers.TryGetValues("Keep-Alive", out var values) ? string.Join(", ", values) : null;

        await Assert.That(hint).IsEqualTo("timeout=5")
            .Because("TransportPolicy.ConnectionIdleMilliseconds is this hint minus undici's 2-second threshold; re-derive it if the pinned server changed the hint");
    }
}
