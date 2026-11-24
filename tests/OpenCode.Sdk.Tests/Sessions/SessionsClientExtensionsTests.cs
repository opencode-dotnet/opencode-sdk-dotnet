using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Sessions.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions;

public sealed class SessionsClientExtensionsTests
{
    private readonly SessionCostFixtures _fixtures = new();

    [Test]
    [Arguments("ses_root")]
    [Arguments("ses_returned")]
    public async Task GetTotalCostAsync_Should_Count_Each_Session_Once_In_A_Cycle(string returnedId)
    {
        var root = _fixtures.Session(returnedId, 1);
        var child = _fixtures.Session("ses_child", 2, root.Id);
        var lists = 0;
        using var scenario = PipelineScenario.Responding(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/session")
            {
                return Answer(SessionCostFixtures.Get(root));
            }

            if (++lists > 20)
            {
                return Answer(SessionCostFixtures.Page([]));
            }

            return Answer(request.RequestUri.Query.Contains("parentID=ses_root", StringComparison.Ordinal)
                ? SessionCostFixtures.Page([child]) : SessionCostFixtures.Page([root, _fixtures.Session("ses_root", 1), child]));
        });

        var total = await new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_root");

        await Assert.That(total).IsEqualTo(3);
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.PathAndQuery))
            .IsEquivalentTo(["/api/session/ses_root", "/api/session?limit=200&order=desc&parentID=ses_root",
                "/api/session?limit=200&order=desc&parentID=ses_child"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Sum_A_Grandchild()
    {
        using var scenario = RootAndLists(1, listQuery => listQuery switch
        {
            var query when query.Contains("parentID=ses_root", StringComparison.Ordinal) =>
                SessionCostFixtures.Page([_fixtures.Session("ses_child", 2, "ses_root")]),
            var query when query.Contains("parentID=ses_child", StringComparison.Ordinal) =>
                SessionCostFixtures.Page([_fixtures.Session("ses_grandchild", 3, "ses_child")]),
            _ => SessionCostFixtures.Page([]),
        });

        var total = await new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_root");

        await Assert.That(total).IsEqualTo(6);
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.PathAndQuery))
            .IsEquivalentTo(["/api/session/ses_root", "/api/session?limit=200&order=desc&parentID=ses_root",
                "/api/session?limit=200&order=desc&parentID=ses_child",
                "/api/session?limit=200&order=desc&parentID=ses_grandchild"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Sum_The_Root_And_Every_Child_Page()
    {
        using var scenario = PipelineScenario.Responding(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/session")
            {
                return Answer(SessionCostFixtures.Get(_fixtures.Session("ses_root", 0.5)));
            }

            if (!request.RequestUri.Query.Contains("parentID=ses_root", StringComparison.Ordinal))
            {
                return Answer(SessionCostFixtures.Page([]));
            }

            return Answer(request.RequestUri.Query.Contains("cursor=", StringComparison.Ordinal)
                ? _fixtures.Page(200, 50) : _fixtures.Page(0, 200, "page_2"));
        });

        var total = await new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_root");
        var expected = new List<string>
        {
            "GET /api/session/ses_root",
            "GET /api/session?limit=200&order=desc&parentID=ses_root",
            "GET /api/session?limit=200&parentID=ses_root&cursor=page_2",
        };
        expected.AddRange(_fixtures.Children(0, 250).Select(static child => "GET /api/session?limit=200&order=desc&parentID=" + child.Id));

        await Assert.That(total).IsEqualTo(250.5);
        await Assert.That(scenario.Requests.Select(static request => request.Method + " " + request.RequestUri!.PathAndQuery))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Return_Only_A_Child_Sessions_Own_Cost()
    {
        using var scenario = PipelineScenario.Responding(request => Answer(request.RequestUri!.AbsolutePath == "/api/session"
            ? SessionCostFixtures.Page([]) : SessionCostFixtures.Get(_fixtures.Session("ses_child", 1.25, "ses_root"))));

        var total = await new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_child");

        await Assert.That(total).IsEqualTo(1.25);
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.PathAndQuery))
            .IsEquivalentTo(["/api/session/ses_child"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Propagate_The_Declared_NotFound_Error()
    {
        using var scenario = PipelineScenario.Responding(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(WireBodyData.SessionNotFoundError) });

        var failure = await Assert.That(new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_9"))
            .Throws<OpenCodeApiException>();

        await Assert.That(failure!.Status).IsEqualTo(404);
        var error = await Assert.That(failure.Error).IsTypeOf<SessionNotFoundError>();
        await Assert.That(error!.SessionId).IsEqualTo("ses_9");
        await Assert.That(error.Message).IsEqualTo("gone");
        await Assert.That(failure.RawBody).IsEqualTo(WireBodyData.SessionNotFoundError);
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.PathAndQuery))
            .IsEquivalentTo(["/api/session/ses_9"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Refuse_NoThrow_At_The_Call()
    {
        using var scenario = PipelineScenario.Responding();

        var failure = Assert.Throws<ArgumentException>(() =>
            _ = new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_root", OpenCodeRequestOptions.NoThrow));

        await Assert.That(failure.ParamName).IsEqualTo("requestOptions");
        // ArgumentException renders its parameter suffix differently on .NET Framework.
#if NETFRAMEWORK
        const string suffix = "\r\nParameter name: requestOptions";
#else
        const string suffix = " (Parameter 'requestOptions')";
#endif
        await Assert.That(failure.Message).IsEqualTo(
            "Session cost totals always throw API errors; NoThrow is not supported." + suffix);
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    [Arguments(" ", "The value cannot be an empty string or composed entirely of whitespace.")]
    [Arguments("..", "Route values must not be dot segments.")]
    public async Task GetTotalCostAsync_Should_Reject_An_Invalid_Id_At_The_Call(string sessionId, string message)
    {
        using var scenario = PipelineScenario.Responding();

        var failure = Assert.Throws<ArgumentException>(() =>
            _ = new SessionsClient(scenario.Pipeline).GetTotalCostAsync(sessionId));

        await Assert.That(failure.ParamName).IsEqualTo("sessionId");
        // ArgumentException renders its parameter suffix differently on .NET Framework.
#if NETFRAMEWORK
        const string suffix = "\r\nParameter name: sessionId";
#else
        const string suffix = " (Parameter 'sessionId')";
#endif
        await Assert.That(failure.Message).IsEqualTo(message + suffix);
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Reject_A_Null_Client()
    {
        var failure = Assert.Throws<ArgumentNullException>(() =>
            _ = SessionsClientExtensions.GetTotalCostAsync(null!, "ses_root"));

        await Assert.That(failure.ParamName).IsEqualTo("client");
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Return_The_Root_Cost_When_There_Are_No_Children()
    {
        using var scenario = PipelineScenario.Responding(request => Answer(request.RequestUri!.AbsolutePath == "/api/session"
            ? SessionCostFixtures.Page([]) : SessionCostFixtures.Get(_fixtures.Session("ses_root", 0.5))));

        var total = await new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_root");

        await Assert.That(total).IsEqualTo(0.5);
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.PathAndQuery))
            .IsEquivalentTo(["/api/session/ses_root", "/api/session?limit=200&order=desc&parentID=ses_root"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Add_The_Root_Before_Children_In_Arrival_Order()
    {
        using var scenario = RootAndLists(1, query => query.Contains("parentID=ses_root", StringComparison.Ordinal)
            ? SessionCostFixtures.Page([_fixtures.Session("ses_small", 1, "ses_root"),
                _fixtures.Session("ses_large", 10_000_000_000_000_000d, "ses_root")])
            : SessionCostFixtures.Page([]));

        var total = await new SessionsClient(scenario.Pipeline).GetTotalCostAsync("ses_root");

        await Assert.That(total).IsEqualTo(10_000_000_000_000_002d);
    }

    [Test]
    public async Task GetTotalCostAsync_Should_Use_The_Generated_Virtual_Methods_On_A_Mocking_Instance()
    {
        var client = MockClient();
        using var caller = new CancellationTokenSource();
        var options = new OpenCodeRequestOptions();

        var total = await client.GetTotalCostAsync("ses_root", options, caller.Token);

        await Assert.That(total).IsEqualTo(2.5);
        await Assert.That(client.SessionId).IsEqualTo("ses_root");
        await Assert.That(client.Request!.ParentId).IsEqualTo(SessionParentFilter.Of("ses_root"));
        await Assert.That(client.Request.Order).IsEqualTo(ListOrder.Descending);
        await Assert.That(client.Request.Limit).IsEqualTo(200);
        SessionParentFilter?[] parents = [SessionParentFilter.Of("ses_root"), SessionParentFilter.Of("ses_child")];
        await Assert.That(client.Calls.Select(static call => call.ParentId)).IsEquivalentTo(parents, CollectionOrdering.Matching);
        foreach (var call in client.Calls)
        {
            await Assert.That(call.Options).IsSameReferenceAs(options);
            await Assert.That(call.Token).IsEqualTo(caller.Token);
        }

        await Assert.That(client.Read.Options).IsSameReferenceAs(options);
        await Assert.That(client.Read.Token).IsEqualTo(caller.Token);
    }

    [Test]
    [Arguments(null)]
    [Arguments("ses_root")]
    [Arguments("ses_child")]
    public async Task GetTotalCostAsync_Should_Propagate_The_Same_Failure(string? listParentId)
    {
        var failure = new InvalidOperationException("Cost read failed.");
        var client = MockClient(failure, listParentId);

        var thrown = await Assert.That(client.GetTotalCostAsync("ses_root")).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(failure);
    }

    private PipelineScenario RootAndLists(double rootCost, Func<string, string> page) =>
        PipelineScenario.Responding(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/session")
            {
                return Answer(SessionCostFixtures.Get(_fixtures.Session("ses_root", rootCost)));
            }

            return Answer(page(request.RequestUri.Query));
        });

    private CostSessionsClient MockClient(Exception? failure = null, string? listParentId = null) =>
        new(new CostSessionClient(_fixtures.Session("ses_root", 0.5), listParentId is null ? failure : null),
            new SessionListResponse { Status = 200, Sessions = [_fixtures.Session("ses_child", 2, "ses_root")], Cursor = new ListCursor() },
            listParentId is null ? null : failure, listParentId);

    private static HttpResponseMessage Answer(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class CostSessionClient(SessionInfo session, Exception? failure) : SessionClient
    {
        public OpenCodeRequestOptions? Options { get; private set; }
        public CancellationToken Token { get; private set; }

        public override Task<SessionResponse> GetAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Options = requestOptions;
            Token = cancellationToken;
            return failure is null ? Task.FromResult(new SessionResponse { Status = 200, Session = session })
                : Task.FromException<SessionResponse>(failure);
        }
    }

    private sealed class CostSessionsClient(CostSessionClient read, SessionListResponse page, Exception? failure, string? listParentId) : SessionsClient
    {
        public CostSessionClient Read => read;
        public string? SessionId { get; private set; }
        public SessionListRequest? Request { get; private set; }
        public List<(SessionParentFilter? ParentId, OpenCodeRequestOptions? Options, CancellationToken Token)> Calls { get; } = [];

        public override SessionClient GetSessionClient(string sessionId)
        {
            SessionId = sessionId;
            return read;
        }

        public override Task<SessionListResponse> ListSessionsAsync(SessionListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((request?.ParentId, requestOptions, cancellationToken));
            if (failure is not null && listParentId is not null && request?.ParentId == SessionParentFilter.Of(listParentId))
            {
                return Task.FromException<SessionListResponse>(failure);
            }

            if (request?.ParentId != SessionParentFilter.Of("ses_root"))
            {
                return Task.FromResult(page with { Sessions = [] });
            }

            Request = request;
            return Task.FromResult(page);
        }
    }
}
