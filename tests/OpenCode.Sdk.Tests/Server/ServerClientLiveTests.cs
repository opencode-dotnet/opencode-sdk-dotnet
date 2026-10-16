using System.Globalization;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Server;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ServerClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task GetInfoAsync_Should_Report_The_Reachable_Fixture_Endpoint(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var response = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.ServerInfo.Urls).IsNotEmpty();
        foreach (var value in response.ServerInfo.Urls)
        {
            var isAbsolute = Uri.TryCreate(value, UriKind.Absolute, out var url);
            await Assert.That(isAbsolute).IsTrue();
            await Assert.That(url).IsNotNull();
            await Assert.That(
                string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)).IsTrue();
            await Assert.That(url.Port).IsGreaterThan(0);
            await Assert.That(url.IsDefaultPort).IsFalse();
        }

        await Assert.That(response.ServerInfo.Urls.Any(value =>
            Uri.TryCreate(value, UriKind.Absolute, out var url) && url == server.Endpoint)).IsTrue();

        Console.WriteLine(
            "server-live: status=" + Number(response.Status) +
            " endpoint=" + server.Endpoint +
            " urls=" + Number(response.ServerInfo.Urls.Count));
    }

    /// <summary>
    /// A pairing code is issued to a credentialed client, redeemed by one that holds nothing, and
    /// the session token it yields opens the server as the password would; the code works once.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task RedeemPairingCodeAsync_Should_Yield_A_Token_The_Server_Accepts_As_The_Password(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        using var anonymous = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = server.Endpoint });

        var issued = await client.Server.CreatePairingCodeAsync(cancellationToken: cancellationToken);

        await Assert.That(issued.Status).IsEqualTo(200);
        await Assert.That(issued.PairingCode.Code).IsNotEmpty();
        await Assert.That(issued.PairingCode.ExpiresIn).IsGreaterThan(0);

        var redeemed = await anonymous.Server.RedeemPairingCodeAsync(issued.PairingCode.Code, cancellationToken: cancellationToken);

        await Assert.That(redeemed.Status).IsEqualTo(200);
        using var paired = new OpenCodeClient(new OpenCodeClientOptions
        {
            Endpoint = server.Endpoint,
            Password = redeemed.PairingSession.Token,
        });
        var info = await paired.Server.GetInfoAsync(cancellationToken: cancellationToken);
        await Assert.That(info.Status).IsEqualTo(200);

        var reused = await anonymous.Server.RedeemPairingCodeAsync(
            issued.PairingCode.Code, OpenCodeRequestOptions.NoThrow, cancellationToken);
        await Assert.That(reused.Status).IsEqualTo(401);
        await Assert.That(reused.Error).IsTypeOf<Models.UnauthorizedError>();

        Console.WriteLine(
            "server-live: pair-status=" + Number(issued.Status) +
            " expires-in=" + issued.PairingCode.ExpiresIn.ToString(CultureInfo.InvariantCulture) +
            " redeem-status=" + Number(redeemed.Status) +
            " paired-info-status=" + Number(info.Status) +
            " reuse-status=" + Number(reused.Status));
    }

    /// <summary>
    /// An uncredentialed request is refused before the API layer runs, and the refusal carries the
    /// declared UnauthorizedError body, so the typed error arrives on the 401.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task GetInfoAsync_Should_Carry_The_Typed_Unauthorized_Error_Without_A_Credential(
        CancellationToken cancellationToken)
    {
        using var anonymous = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = server.Endpoint });

        var refused = await anonymous.Server.GetInfoAsync(OpenCodeRequestOptions.NoThrow, cancellationToken);

        await Assert.That(refused.Status).IsEqualTo(401);
        await Assert.That(refused.Error).IsTypeOf<Models.UnauthorizedError>();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
