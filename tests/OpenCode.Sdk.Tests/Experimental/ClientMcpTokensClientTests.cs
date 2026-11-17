using System.Net;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpTokensClientTests
{
    private static readonly ExperimentalMcpClientTokenRequest Request = new()
    {
        Name = "browser_app",
        Location = new LocationSelector { Directory = "/repo & ü" },
    };

    [Test]
    public async Task CreateClientMcpConnectTokenAsync_Should_Send_The_Declared_Token_Request()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new JsonObject { ["ticket"] = nonce, ["expires_in"] = 60 }.ToJsonString();
        using var scenario = ContractScenario.RespondingToCredentialed(HttpStatusCode.OK, WireBodyData.LocationEnvelope(payload), "synthetic");

        var response = await scenario.Client.Experimental.CreateClientMcpConnectTokenAsync(Request);

        await Assert.That(scenario.Requests.Count).IsEqualTo(1);
        var sent = scenario.Requests.Single();
        await Assert.That(sent.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(sent.RequestUri!.AbsolutePath).IsEqualTo("/api/experimental/client-mcp/token");
        await Assert.That(sent.RequestUri.Query).IsEqualTo("?location[directory]=%2Frepo%20%26%20%C3%BC&name=browser_app");
        await Assert.That(sent.Authorization).IsEqualTo("Basic b3BlbmNvZGU6c3ludGhldGlj");
        await Assert.That(sent.Headers["x-opencode-ticket"]).IsEqualTo("1");
        await Assert.That(sent.Body).IsNull();
        await Assert.That(sent.ContentType).IsNull();
        await Assert.That(response.ConnectToken.Ticket).IsEqualTo(nonce);
        await Assert.That(response.ConnectToken.ExpiresIn).IsEqualTo(60);
        await Assert.That(response.Location.Directory).IsEqualTo("/repo");
        await ContractScenario.AssertSuccessSpineAsync(response, 200);
    }

    [Test]
    [Arguments(400, WireBodyData.InvalidRequestError, typeof(InvalidRequestError))]
    [Arguments(401, WireBodyData.UnauthorizedError, typeof(UnauthorizedError))]
    [Arguments(403, WireBodyData.ForbiddenError, typeof(ForbiddenError))]
    [Arguments(404, null, typeof(LocationNotFoundError))]
    public async Task CreateClientMcpConnectTokenAsync_Should_Keep_Declared_Failures_And_Caller_Cancellation(
        int status, string? body, Type errorType)
    {
        body ??= new JsonObject
        {
            ["_tag"] = "LocationNotFoundError",
            ["location"] = new JsonObject { ["directory"] = "/gone" },
            ["message"] = "gone",
        }.ToJsonString();
        using var scenario = ContractScenario.Responding((HttpStatusCode)status, body);

        var failure = await Assert.That(async () => _ = await scenario.Client.Experimental.CreateClientMcpConnectTokenAsync(Request))
            .Throws<OpenCodeApiException>();
        await Assert.That(failure!.Status).IsEqualTo(status);
        await Assert.That(failure.Error!.GetType()).IsEqualTo(errorType);
        await Assert.That(failure.RawBody).IsEqualTo(body);
        var response = await scenario.Client.Experimental.CreateClientMcpConnectTokenAsync(Request, OpenCodeRequestOptions.NoThrow);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(status);
        await Assert.That(response.Error!.GetType()).IsEqualTo(errorType);
        await Assert.That(response.RawBody).IsEqualTo(body);
    }

    [Test]
    public async Task CreateClientMcpConnectTokenAsync_Should_Keep_The_Caller_Token()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(WireBodyData.PersistentPtyConnectTokenBody));
        var cancelled = await Assert.That(async () => _ = await scenario.Client.Experimental.CreateClientMcpConnectTokenAsync(
            Request, cancellationToken: caller.Token)).Throws<OperationCanceledException>();
        await Assert.That(cancelled!.CancellationToken == caller.Token).IsTrue();
    }

    [Test]
    public async Task CreateClientMcpConnectTokenAsync_Should_Keep_Transport_Failures_Under_NoThrow()
    {
        var cause = new HttpRequestException("synthetic transport failure");
        using var transport = ContractScenario.Responding(_ => throw cause);
        var failure = await Assert.That(async () => _ = await transport.Client.Experimental.CreateClientMcpConnectTokenAsync(
            Request, OpenCodeRequestOptions.NoThrow)).Throws<OpenCodeTransportException>();
        await Assert.That(failure!.InnerException).IsSameReferenceAs(cause);
    }
}
