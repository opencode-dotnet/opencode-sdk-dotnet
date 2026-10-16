using System.Globalization;
using System.Net;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Pins the exact-pin fixture's external-endpoint mode: attaching to an
/// operator-supplied server instead of spawning one, and refusing fail-fast when that server does
/// not answer a health probe. Drives the fixture through its internal
/// <see cref="ExternalServerEndpoint"/> constructor over a real loopback socket, not the shared
/// per-session instance <see cref="PinnedServerFixtureTests"/> shares.
/// </summary>
[NotInParallel]
public sealed class PinnedOpenCodeServerFixtureExternalModeTests
{
    [Test]
    [Timeout(15_000)]
    public async Task Fixture_Should_Attach_To_An_External_Endpoint_When_One_Is_Supplied(CancellationToken cancellationToken)
    {
        await using var server = LoopbackHttpServer.Start(static path => path switch
        {
            "/api/info" => new LoopbackHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
                Body = WireBodyData.InfoOk,
            },
            _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.InternalServerError },
        });
        var fixture = new PinnedOpenCodeServerFixture(new ExternalServerEndpoint(server.Endpoint, "any-password"));

        await fixture.InitializeAsync();
        try
        {
            await Assert.That(fixture.Endpoint).IsEqualTo(server.Endpoint);
            using var client = fixture.CreateClient();

            var health = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

            await Assert.That(health.Status).IsEqualTo(200);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Test]
    public async Task Fixture_Should_Refuse_An_External_Endpoint_That_Does_Not_Answer_Health()
    {
        await using var server = LoopbackHttpServer.Start(
            static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.InternalServerError });
        var fixture = new PinnedOpenCodeServerFixture(new ExternalServerEndpoint(server.Endpoint, "any-password"));

        var exception = await Assert.That(fixture.InitializeAsync).Throws<InvalidOperationException>();

        // The server answered (with a 500), so the message must name the real cause rather than
        // claim a timeout that never happened - the failure this fixture surfaces must be
        // trustworthy enough for an operator to act on directly.
        await Assert.That(exception!.Message).Contains(server.Endpoint.ToString());
        await Assert.That(exception.Message).Contains("500");
        await Assert.That(exception.Message).DoesNotContain("did not answer a health probe");
        await fixture.DisposeAsync();
    }

    [Test]
    public async Task Fixture_Should_Refuse_An_Unreachable_External_Endpoint()
    {
        var port = LoopbackPortReservation.Reserve();
        var endpoint = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
        var fixture = new PinnedOpenCodeServerFixture(new ExternalServerEndpoint(endpoint, "any-password"));

        var exception = await Assert.That(fixture.InitializeAsync).Throws<InvalidOperationException>();

        // Nothing is listening on the reserved port, so this is a connection failure, not a
        // timeout and not an HTTP error status - the message must not claim either of those.
        await Assert.That(exception!.Message).Contains(endpoint.ToString());
        await Assert.That(exception.Message).DoesNotContain("did not answer a health probe");
        await Assert.That(exception.Message).DoesNotContain("answered the health probe");
        await fixture.DisposeAsync();
    }
}
