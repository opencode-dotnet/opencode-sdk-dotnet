using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.PersistentPtys;

public sealed class PersistentPtysClientContractTests
{
    private const string TicketHeader = "x-opencode-ticket";

    private const string PtyId = "pty_persistent_7";

    private static readonly Uri SessionTerminals = new("http://localhost:4096/api/experimental/session/ses_1/terminal");

    private static readonly Uri Terminal = new($"http://localhost:4096/api/experimental/persistent-pty/{PtyId}");

    public static IEnumerable<Func<(string Name, Func<OpenCodeClient, Task> Door)>> EveryDoor() =>
    [
        static () => ("list", client => client.PersistentPtys.ListPersistentPtysAsync("ses_1")),
        static () => ("create", client => client.PersistentPtys.CreatePersistentPtyAsync("ses_1", CreateRequest())),
        static () => ("read", client => client.PersistentPtys.ReadAsync("ses_1")),
        static () => ("handoff", client => client.PersistentPtys.HandoffAsync()),
        static () => ("shutdown", client => client.PersistentPtys.ShutdownAsync()),
        static () => ("get", client => client.PersistentPtys.GetPersistentPtyClient(PtyId).GetAsync()),
        static () => ("update", client => client.PersistentPtys.GetPersistentPtyClient(PtyId).UpdateAsync(UpdateRequest())),
        static () => ("remove", client => client.PersistentPtys.GetPersistentPtyClient(PtyId).RemoveAsync()),
        static () => ("snapshot", client => client.PersistentPtys.GetPersistentPtyClient(PtyId).GetSnapshotAsync()),
        static () => ("connectToken", client => client.PersistentPtys.GetPersistentPtyClient(PtyId).CreateConnectTokenAsync()),
    ];

    [Test]
    public async Task ListPersistentPtysAsync_Should_Materialize_An_Empty_Page_When_The_Daemon_Is_Absent()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.PersistentPtys.ListPersistentPtysAsync("ses_1");

        await Assert.That(response.PersistentPtys.Count).IsEqualTo(0);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(request.RequestUri).IsEqualTo(SessionTerminals);
    }

    [Test]
    public async Task ListPersistentPtysAsync_Should_Materialize_The_Session_Terminals()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope($"[{payload}]"));

        var response = await scenario.Client.PersistentPtys.ListPersistentPtysAsync("ses_1");

        var terminal = response.PersistentPtys.Single();
        await Assert.That(terminal.Id).IsEqualTo(PtyId);
        await Assert.That(terminal.SessionId).IsEqualTo("ses_1");
    }

    [Test]
    public async Task ListPersistentPtysAsync_Should_Throw_The_Declared_503_Daemon_Arm()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.ListPersistentPtysAsync("ses_1"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(503);
        await Assert.That(exception.Error).IsTypeOf<ServiceUnavailableError>();
    }

    [Test]
    public async Task CreatePersistentPtyAsync_Should_Send_The_Typed_Body_And_Return_The_Typed_Terminal()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.PersistentPtys.CreatePersistentPtyAsync("ses_1", CreateRequest());

        await Assert.That(response.PersistentPty.Id).IsEqualTo(PtyId);
        await Assert.That(response.PersistentPty.SessionId).IsEqualTo("ses_1");
        await Assert.That(response.PersistentPty.Output.Tail).IsEqualTo(42);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri).IsEqualTo(SessionTerminals);
        await Assert.That(request.Body).IsEqualTo("{\"args\":[\"-l\"],\"title\":\"sdk terminal\",\"env\":{}}");
    }

    [Test]
    public async Task CreatePersistentPtyAsync_Should_Send_The_Optional_Members_The_Request_Carries()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        _ = await scenario.Client.PersistentPtys.CreatePersistentPtyAsync("ses_1", CreateRequest() with
        {
            Cwd = "/repo",
            Size = new PersistentPtyCreateInputSize { Cols = 100, Rows = 30 },
        });

        await Assert.That(scenario.Requests.Single().Body).IsEqualTo(
            "{\"args\":[\"-l\"],\"cwd\":\"/repo\",\"title\":\"sdk terminal\",\"env\":{},\"size\":{\"cols\":100,\"rows\":30}}");
    }

    [Test]
    public async Task CreatePersistentPtyAsync_Should_Throw_The_Declared_503_Daemon_Arm()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.CreatePersistentPtyAsync("ses_1", CreateRequest()))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(503);
        var error = (ServiceUnavailableError)exception.Error!;
        await Assert.That(error.Service).IsEqualTo("opencode-pty");
    }

    [Test]
    public async Task CreatePersistentPtyAsync_Should_Return_The_503_Daemon_Arm_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError);

        var response = await scenario.Client.PersistentPtys.CreatePersistentPtyAsync(
            "ses_1", CreateRequest(), OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(503);
        await Assert.That(response.Error).IsTypeOf<ServiceUnavailableError>();
        await Assert.That(response.RawBody).IsEqualTo(WireBodyData.ServiceUnavailableError);
    }

    [Test]
    public async Task ReadAsync_Should_Materialize_A_Null_Payload_As_No_Current_Terminal()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("null"));

        var response = await scenario.Client.PersistentPtys.ReadAsync("ses_1", new PersistentPtyReadRequest { Lines = "40" });

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Read).IsNull();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/session/ses_1/terminal/read?lines=40"));
    }

    [Test]
    public async Task ReadAsync_Should_Materialize_The_Current_Terminal_Screen()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty-read.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.PersistentPtys.ReadAsync("ses_1");

        await Assert.That(response.Read!.PtyId).IsEqualTo(PtyId);
        await Assert.That(response.Read.Screen.Text).Contains("hello");
        await Assert.That(response.Read.Screen.Cursor.Y).IsEqualTo(2);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/session/ses_1/terminal/read"));
    }

    [Test]
    public async Task ReadAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.ReadAsync("ses_1", new PersistentPtyReadRequest { Lines = "0" }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task HandoffAsync_Should_Materialize_A_Null_Payload_When_This_Server_Owns_No_Daemon()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, "{\"handoff\":null}");

        var response = await scenario.Client.PersistentPtys.HandoffAsync();

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Handoff).IsNull();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/persistent-pty/handoff"));
    }

    [Test]
    public async Task HandoffAsync_Should_Materialize_The_Prepared_Handoff()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty-handoff.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, $"{{\"handoff\":{payload}}}");

        var response = await scenario.Client.PersistentPtys.HandoffAsync();

        await Assert.That(response.Handoff!.Ticket).IsEqualTo("hnd_1");
        await Assert.That(response.Handoff.InstanceId).IsEqualTo("inst_1");
        await Assert.That(response.Handoff.ExpiresAt).IsEqualTo(1756450000000d);
    }

    [Test]
    public async Task HandoffAsync_Should_Throw_The_Declared_503_Daemon_Arm()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.HandoffAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(503);
        await Assert.That(exception.Error).IsTypeOf<ServiceUnavailableError>();
    }

    [Test]
    public async Task ShutdownAsync_Should_Answer_204_Even_When_No_Daemon_Is_Running()
    {
        using var scenario = ContractScenario.Responding(static _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var response = await scenario.Client.PersistentPtys.ShutdownAsync();

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/persistent-pty/shutdown"));
    }

    [Test]
    public async Task ShutdownAsync_Should_Throw_The_Declared_503_Daemon_Arm()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.ShutdownAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(503);
        await Assert.That(exception.Error).IsTypeOf<ServiceUnavailableError>();
    }

    [Test]
    public async Task GetPersistentPtyAsync_Should_Materialize_The_Bound_Terminal()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).GetAsync();

        await Assert.That(response.PersistentPty.Id).IsEqualTo(PtyId);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(request.RequestUri).IsEqualTo(Terminal);
    }

    [Test]
    public async Task GetPersistentPtyAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.PtyNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).GetAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<PtyNotFoundError>();
    }

    [Test]
    public async Task UpdatePersistentPtyAsync_Should_Send_The_Resize_Body()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId)
            .UpdateAsync(UpdateRequest());

        await Assert.That(response.PersistentPty.Id).IsEqualTo(PtyId);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(request.RequestUri).IsEqualTo(Terminal);
        await Assert.That(request.Body).IsEqualTo("{\"size\":{\"cols\":120,\"rows\":40}}");
    }

    [Test]
    public async Task UpdatePersistentPtyAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.PtyNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId)
                .UpdateAsync(UpdateRequest()))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<PtyNotFoundError>();
    }

    [Test]
    public async Task RemovePersistentPtyAsync_Should_Answer_204_On_The_Delete_Route()
    {
        using var scenario = ContractScenario.Responding(static _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var response = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).RemoveAsync();

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri).IsEqualTo(Terminal);
    }

    [Test]
    public async Task RemovePersistentPtyAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.PtyNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).RemoveAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<PtyNotFoundError>();
    }

    [Test]
    public async Task GetSnapshotAsync_Should_Materialize_The_Checkpoint_As_Bytes()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-persistent-pty-snapshot.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).GetSnapshotAsync();

        await Assert.That(response.Snapshot.Checkpoint.ToArray()).IsEquivalentTo(new byte[] { 0x1B, 0x63 });
        await Assert.That(response.Snapshot.Cursor.Y).IsEqualTo(2);
        await Assert.That(response.Snapshot.Info.Size.Cols).IsEqualTo(80);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri($"http://localhost:4096/api/experimental/persistent-pty/{PtyId}/snapshot"));
    }

    [Test]
    public async Task GetSnapshotAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.PtyNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).GetSnapshotAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<PtyNotFoundError>();
    }

    [Test]
    public async Task CreateConnectTokenAsync_Should_Send_The_Ticket_Sentinel_And_Materialize_The_Token()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.Envelope(WireBodyData.PersistentPtyConnectTokenBody));

        var response = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).CreateConnectTokenAsync();

        await Assert.That(response.ConnectToken.Ticket).IsEqualTo("tkt_p1");
        await Assert.That(response.ConnectToken.ExpiresIn).IsEqualTo(60);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri($"http://localhost:4096/api/experimental/persistent-pty/{PtyId}/connect-token"));
        await Assert.That(request.Headers[TicketHeader]).IsEqualTo("1");
        await Assert.That(request.Body).IsNull();
    }

    [Test]
    public async Task CreateConnectTokenAsync_Should_Throw_The_Declared_403_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Forbidden, WireBodyData.ForbiddenError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).CreateConnectTokenAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(403);
        await Assert.That(exception.Error).IsTypeOf<ForbiddenError>();
    }

    [Test]
    public async Task CreateConnectTokenAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.PtyNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.PersistentPtys.GetPersistentPtyClient(PtyId).CreateConnectTokenAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<PtyNotFoundError>();
    }

    /// <summary>
    /// The ticket header is the token door's alone (ADR-0021): it rides that one request and no
    /// other. The declared 400 answers every door, so the responder does not have to satisfy ten
    /// different success shapes to reach the header the request already carried.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(EveryDoor))]
    public async Task Only_The_Token_Door_Should_Send_The_Ticket_Header((string Name, Func<OpenCodeClient, Task> Door) door)
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        _ = await Assert.That(async () => await door.Door(scenario.Client)).Throws<OpenCodeApiException>();

        var request = scenario.Requests.Single();
        await Assert.That(request.Headers.ContainsKey(TicketHeader)).IsEqualTo(door.Name is "connectToken");
    }

    [Test]
    [MethodDataSource(nameof(EveryDoor))]
    public async Task Every_Door_Should_Throw_The_Declared_401_Error((string Name, Func<OpenCodeClient, Task> Door) door)
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var exception = await Assert.That(async () => await door.Door(scenario.Client)).Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(401);
        await Assert.That(exception.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    [MethodDataSource(nameof(EveryDoor))]
    public async Task Every_Door_Should_Throw_The_Declared_400_Error((string Name, Func<OpenCodeClient, Task> Door) door)
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert.That(async () => await door.Door(scenario.Client)).Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task GetPersistentPtyClient_Should_Refuse_A_Dot_Segment_Route_Value()
    {
        using var scenario = ContractScenario.Responding();

        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.PersistentPtys.GetPersistentPtyClient("."));
        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.PersistentPtys.GetPersistentPtyClient(".."));
        _ = Assert.Throws<ArgumentException>(() => _ = scenario.Client.PersistentPtys.GetPersistentPtyClient(" "));

        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    public async Task PersistentPtys_Should_Be_The_Root_Client_Family_Accessor()
    {
        using var scenario = ContractScenario.Responding();

        await Assert.That(scenario.Client.PersistentPtys).IsNotNull();
        await Assert.That(scenario.Client.PersistentPtys).IsSameReferenceAs(scenario.Client.PersistentPtys);
    }

    [Test]
    public async Task Family_Mock_Seams_Should_Stay_Overridable()
    {
        var terminals = new MockPersistentPtysClient();
        var terminal = new MockPersistentPtyClient();

        await Assert.That((await terminals.ListPersistentPtysAsync("ses_1")).PersistentPtys).IsEmpty();
        await Assert.That(terminals.GetPersistentPtyClient(PtyId)).IsSameReferenceAs(MockPersistentPtysClient.Handle);
        await Assert.That((await terminal.CreateConnectTokenAsync()).ConnectToken.Ticket).IsEqualTo("mocked");
    }

    [Test]
    public async Task Family_Mock_Seams_Should_Fail_Instructively_Without_An_Override()
    {
        var terminals = new UnoverriddenPersistentPtysClient();
        var terminal = new UnoverriddenPersistentPtyClient();

        var collection = await Assert
            .That(async () => _ = await terminals.ListPersistentPtysAsync("ses_1"))
            .Throws<InvalidOperationException>();
        var handle = await Assert
            .That(async () => _ = await terminal.CreateConnectTokenAsync())
            .Throws<InvalidOperationException>();

        await Assert.That(collection!.Message).Contains("mocking constructor");
        await Assert.That(handle!.Message).Contains("mocking constructor");
    }

    private static PersistentPtyCreateRequest CreateRequest() => new()
    {
        Args = ["-l"],
        Title = "sdk terminal",
        Env = new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static PersistentPtyUpdateRequest UpdateRequest() => new()
    {
        Size = new PersistentPtyUpdateInputSize { Cols = 120, Rows = 40 },
    };

    private sealed class MockPersistentPtysClient : PersistentPtysClient
    {
        public static readonly PersistentPtyClient Handle = new MockPersistentPtyClient();

        public override Task<PersistentPtyListResponse> ListPersistentPtysAsync(string sessionId,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PersistentPtyListResponse
            {
                Status = 200,
                PersistentPtys = [],
            });

        public override PersistentPtyClient GetPersistentPtyClient(string ptyId) => Handle;
    }

    private sealed class MockPersistentPtyClient : PersistentPtyClient
    {
        public override Task<PersistentPtyConnectTokenResponse> CreateConnectTokenAsync(
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PersistentPtyConnectTokenResponse
            {
                Status = 200,
                ConnectToken = new PtyTicketConnectToken { Ticket = "mocked", ExpiresIn = 60 },
            });
    }

    private sealed class UnoverriddenPersistentPtysClient : PersistentPtysClient
    {
    }

    private sealed class UnoverriddenPersistentPtyClient : PersistentPtyClient
    {
    }
}
