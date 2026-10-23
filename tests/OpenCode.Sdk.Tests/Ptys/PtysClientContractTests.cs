using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtysClientContractTests
{
    private static async Task AssertTicketHeaderAbsentAsync(IReadOnlyDictionary<string, string> headers) =>
        await Assert.That(headers).DoesNotContainKey(TicketHeader);

    private const string TicketHeader = "x-opencode-ticket";

    private const string ConnectTokenBody = "{\"ticket\":\"tkt_1\",\"expires_in\":30}";

    [Test]
    public async Task CreatePtyAsync_Should_Send_The_Typed_Body_And_Return_The_Typed_Pty()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-pty.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(payload));

        var response = await scenario.Client.Ptys.CreatePtyAsync(new PtyCreateRequest
        {
            Command = "pwsh",
            Title = "probe shell",
        });

        await Assert.That(response.Pty.Id).IsEqualTo("pty_100");
        await Assert.That(response.Pty.Status).IsEqualTo(PtyStatus.Running);
        await Assert.That(response.Pty.Pid).IsEqualTo(4242);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/pty"));
        await Assert.That(request.Body).IsEqualTo("{\"command\":\"pwsh\",\"title\":\"probe shell\"}");
    }

    [Test]
    public async Task GetAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.PtyNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Ptys.GetPtyClient("pty_9").GetAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<PtyNotFoundError>();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/pty/pty_9"));
    }

    [Test]
    public async Task UpdateAsync_Should_Send_The_Empty_Body_When_Omitted()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-pty.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(payload));

        var response = await scenario.Client.Ptys.GetPtyClient("pty_100").UpdateAsync();

        await Assert.That(response.Pty.Id).IsEqualTo("pty_100");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/pty/pty_100"));
        await Assert.That(request.ContentType).IsEqualTo("application/json");
        await Assert.That(request.Body).IsEqualTo("{}");
    }

    [Test]
    public async Task ListPtysAsync_Should_Hit_The_Collection_Route_And_Materialize_The_Page()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-pty.json");
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.LocationEnvelope($"[{payload}]"));

        var response = await scenario.Client.Ptys.ListPtysAsync();

        await Assert.That(response.Ptys.Single().Id).IsEqualTo("pty_100");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/pty"));
    }

    [Test]
    public async Task ListPtysAsync_Should_Carry_The_Location_Query_The_Request_Shapes()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-pty.json");
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.LocationEnvelope($"[{payload}]"));

        _ = await scenario.Client.Ptys.ListPtysAsync(new PtyListRequest
        {
            Location = new LocationSelector { Directory = "/repo" },
        });

        await Assert.That(scenario.Requests.Single().RequestUri!.Query)
            .IsEqualTo("?location[directory]=%2Frepo");
    }

    [Test]
    public async Task CreateConnectTokenAsync_Should_Send_The_Ticket_Sentinel_And_Materialize_The_Token()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.LocationEnvelope(ConnectTokenBody));

        var response = await scenario.Client.Ptys.GetPtyClient("pty_100").CreateConnectTokenAsync();

        await Assert.That(response.ConnectToken.Ticket).IsEqualTo("tkt_1");
        await Assert.That(response.ConnectToken.ExpiresIn).IsEqualTo(30);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/pty/pty_100/connect-token"));
        await Assert.That(request.Headers[TicketHeader]).IsEqualTo("1");
        await Assert.That(request.Body).IsNull();
        await Assert.That(request.ContentType).IsNull();
    }

    [Test]
    public async Task CreateConnectTokenAsync_Should_Carry_The_Location_Query_The_Request_Shapes()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.LocationEnvelope(ConnectTokenBody));

        _ = await scenario.Client.Ptys.GetPtyClient("pty_100").CreateConnectTokenAsync(new PtyConnectTokenRequest
        {
            Location = new LocationSelector { Directory = "/repo" },
        });

        var request = scenario.Requests.Single();
        await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo("/api/pty/pty_100/connect-token");
        await Assert.That(request.RequestUri.Query).IsEqualTo("?location[directory]=%2Frepo");
        await Assert.That(request.Headers[TicketHeader]).IsEqualTo("1");
    }

    [Test]
    public async Task CreateConnectTokenAsync_Should_Send_The_Ambient_And_PerCall_Location_Headers()
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(WireBodyData.LocationEnvelope(ConnectTokenBody)),
        });
        using var httpClient = new HttpClient(handler);
        using var client = new OpenCodeClient(httpClient, new OpenCodeClientOptions
        {
            Endpoint = ContractScenario.Endpoint,
            Location = new LocationSelector { Directory = "/amb/dir" },
        });

        _ = await client.Ptys.GetPtyClient("pty_100").CreateConnectTokenAsync(
            request: null,
            new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/per/dir" } });

        var request = handler.Requests.Single();
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Fper%2Fdir");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
        await Assert.That(request.Headers[TicketHeader]).IsEqualTo("1");
    }

    [Test]
    public async Task No_Family_Method_Other_Than_The_Token_Door_Should_Send_The_Ticket_Header()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-pty.json");

        // Remove declares 204 alone, so a blanket 200 would fail materialization before the
        // header assertion ever ran.
        using var scenario = ContractScenario.Responding(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(WireBodyData.LocationEnvelope(payload)),
            });
        var ptys = scenario.Client.Ptys;
        var pty = ptys.GetPtyClient("pty_100");

        _ = await ptys.CreatePtyAsync();
        _ = await pty.GetAsync();
        _ = await pty.UpdateAsync();
        _ = await pty.RemoveAsync();

        await Assert.That(scenario.Requests.Count).IsEqualTo(4);
        foreach (var request in scenario.Requests)
        {
            await AssertTicketHeaderAbsentAsync(request.Headers);
        }
    }

    [Test]
    public async Task ListPtysAsync_Should_Not_Send_The_Ticket_Header()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-pty.json");
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.LocationEnvelope($"[{payload}]"));

        _ = await scenario.Client.Ptys.ListPtysAsync();

        await AssertTicketHeaderAbsentAsync(scenario.Requests.Single().Headers);
    }

    [Test]
    public async Task GetPtyClient_Should_Refuse_A_Dot_Segment_Route_Value()
    {
        using var scenario = ContractScenario.Responding();

        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.Ptys.GetPtyClient("."));
        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.Ptys.GetPtyClient(".."));
        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.Ptys.GetPtyClient(" "));
    }

    [Test]
    public async Task Family_Mock_Seams_Should_Stay_Overridable()
    {
        var ptys = new MockPtysClient();
        var pty = new MockPtyClient();

        await Assert.That((await ptys.ListPtysAsync()).Ptys).IsEmpty();
        await Assert.That(ptys.GetPtyClient("pty_100")).IsSameReferenceAs(MockPtysClient.Handle);
        await Assert.That((await pty.CreateConnectTokenAsync()).ConnectToken.Ticket).IsEqualTo("mocked");
    }

    [Test]
    public async Task Family_Mock_Seams_Should_Fail_Instructively_Without_An_Override()
    {
        var ptys = new UnoverriddenPtysClient();
        var pty = new UnoverriddenPtyClient();

        var collection = await Assert.That(async () => _ = await ptys.ListPtysAsync()).Throws<InvalidOperationException>();
        var handle = await Assert.That(async () => _ = await pty.CreateConnectTokenAsync()).Throws<InvalidOperationException>();
        var session = await Assert.That(async () => _ = await pty.ConnectAsync()).Throws<InvalidOperationException>();

        await Assert.That(collection!.Message).Contains("mocking constructor");
        await Assert.That(handle!.Message).Contains("mocking constructor");
        await Assert.That(session!.Message).Contains("mocking constructor");
    }

    private sealed class MockPtysClient : PtysClient
    {
        public static readonly PtyClient Handle = new MockPtyClient();

        public override Task<PtyListResponse> ListPtysAsync(PtyListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PtyListResponse
            {
                Status = 200,
                Location = MockedLocation,
                Ptys = [],
            });

        public override PtyClient GetPtyClient(string ptyId) => Handle;
    }

    private sealed class MockPtyClient : PtyClient
    {
        public override Task<PtyConnectTokenResponse> CreateConnectTokenAsync(PtyConnectTokenRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PtyConnectTokenResponse
            {
                Status = 200,
                Location = MockedLocation,
                ConnectToken = new PtyTicketConnectToken { Ticket = "mocked", ExpiresIn = 30 },
            });
    }

    private sealed class UnoverriddenPtysClient : PtysClient
    {
    }

    private sealed class UnoverriddenPtyClient : PtyClient
    {
    }

    private static LocationPublicRef MockedLocation => new()
    {
        Directory = "/repo",
    };
}
