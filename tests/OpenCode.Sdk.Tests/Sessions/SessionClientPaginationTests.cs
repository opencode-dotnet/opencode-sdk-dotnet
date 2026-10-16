using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Sessions;

public sealed class SessionClientPaginationTests
{
    [Test]
    public async Task EnumerateMessagesAsync_Should_Use_The_Virtual_Page_Method_And_Preserve_An_Empty_Cursor()
    {
        var client = new StubSessionClient(Page(next: string.Empty), Page(next: null));
        var initialRequest = new SessionMessageListRequest
        {
            Limit = 2,
            Order = ListOrder.Descending,
            Cursor = "cur_start",
        };
        var itemCount = 0;

        await foreach (var _ in client
                           .EnumerateMessagesAsync(initialRequest, cancellationToken: CancellationToken.None)
                           .WithCancellation(CancellationToken.None))
        {
            itemCount++;
        }

        await Assert.That(itemCount).IsEqualTo(0);
        await Assert.That(client.Requests.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(client.Requests[0], initialRequest)).IsTrue();
        await Assert.That(client.Requests[1]?.Limit).IsEqualTo(2);
        await Assert.That(client.Requests[1]?.Order).IsNull();
        await Assert.That(client.Requests[1]?.Cursor).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task EnumerateMessagesAsync_Should_Yield_Every_Buffered_Item_In_Page_Order()
    {
        var client = new StubSessionClient(
            Page("cur_1", Message("msg_1"), Message("msg_2")),
            Page(next: null, Message("msg_3")));
        var received = new List<string>();

        await foreach (var message in client.EnumerateMessagesAsync())
        {
            received.Add(((SessionMessageUser)message).Id);
        }

        await Assert.That(received.SequenceEqual(["msg_1", "msg_2", "msg_3"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Pages_Should_Yield_Each_Page_Envelope_Of_The_Same_Traversal()
    {
        var client = new StubSessionClient(
            Page("cur_1", Message("msg_1")),
            Page(next: null, Message("msg_2")));
        var received = new List<SessionMessageListResponse>();

        await foreach (var page in client.EnumerateMessagesAsync().Pages.WithCancellation(CancellationToken.None))
        {
            received.Add(page);
        }

        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received[0].Cursor.Next).IsEqualTo("cur_1");
        await Assert.That(received[1].Cursor.Next).IsNull();
        await Assert.That(received.All(static page => page.Status == 200)).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Pages_Should_Send_Its_Own_Requests_When_The_Sequence_Is_Enumerated_Twice()
    {
        var client = new StubSessionClient(
            Page("cur_1", Message("msg_1")),
            Page(next: null, Message("msg_2")),
            Page("cur_1", Message("msg_1")),
            Page(next: null, Message("msg_2")));
        var sequence = client.EnumerateMessagesAsync();
        var items = 0;
        var pages = 0;

        await foreach (var _ in sequence.WithCancellation(CancellationToken.None))
        {
            items++;
        }

        await foreach (var _ in sequence.Pages.WithCancellation(CancellationToken.None))
        {
            pages++;
        }

        await Assert.That(items).IsEqualTo(2);
        await Assert.That(pages).IsEqualTo(2);
        await Assert.That(client.Requests.Count).IsEqualTo(4);
    }

    [Test]
    public async Task GetAsyncEnumerator_Should_Observe_The_Token_Given_To_The_Companion()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new StubSessionClient(Page(next: null, Message("msg_1"), Message("msg_2")));
        await using var enumerator = client
            .EnumerateMessagesAsync(cancellationToken: cancellation.Token)
            .GetAsyncEnumerator();

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await cancellation.CancelOnWorkerAsync();

        await Assert.That(client.Tokens[0].IsCancellationRequested).IsTrue();
        _ = await Assert.That(async () => _ = await enumerator.MoveNextAsync()).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task GetAsyncEnumerator_Should_Observe_The_Token_Given_To_The_Enumerator()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new StubSessionClient(Page(next: null, Message("msg_1"), Message("msg_2")));
        await using var enumerator = client
            .EnumerateMessagesAsync(cancellationToken: CancellationToken.None)
            .GetAsyncEnumerator(cancellation.Token);

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await cancellation.CancelOnWorkerAsync();

        await Assert.That(client.Tokens[0].IsCancellationRequested).IsTrue();
        _ = await Assert.That(async () => _ = await enumerator.MoveNextAsync()).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Pages_Should_Surface_The_Failure_Of_The_Page_That_Raised_It()
    {
        var failure = new InvalidOperationException("page two failed");
        var client = new FailingSessionClient(Page("cur_1", Message("msg_1")), failure);
        await using var enumerator = client.EnumerateMessagesAsync().Pages.GetAsyncEnumerator();

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current.Cursor.Next).IsEqualTo("cur_1");

        var thrown = await Assert.That(async () => _ = await enumerator.MoveNextAsync()).Throws<InvalidOperationException>();
        await Assert.That(thrown).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task EnumerateSessionsAsync_Should_Carry_Every_Filter_And_Drop_Order_On_A_Continuation()
    {
        var client = new StubSessionsClient(SessionPage("cur_1"), SessionPage(next: null));
        var initialRequest = new SessionListRequest
        {
            Limit = 3,
            Order = ListOrder.Descending,
            Search = "needle",
            Project = "prj_1",
        };

        await foreach (var _ in client.EnumerateSessionsAsync(initialRequest))
        {
            // The traversal itself is the subject; the canned pages carry no sessions.
        }

        await Assert.That(client.Requests.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(client.Requests[0], initialRequest)).IsTrue();
        await Assert.That(client.Requests[1]?.Limit).IsEqualTo(3);
        await Assert.That(client.Requests[1]?.Search).IsEqualTo("needle");
        await Assert.That(client.Requests[1]?.Project).IsEqualTo("prj_1");
        await Assert.That(client.Requests[1]?.Cursor).IsEqualTo("cur_1");
        await Assert.That(client.Requests[1]?.Order).IsNull();
    }

    [Test]
    public async Task EnumerateSessionsAsync_Should_Start_A_Continuation_From_An_Absent_Request()
    {
        var client = new StubSessionsClient(SessionPage("cur_1"), SessionPage(next: null));

        await foreach (var _ in client.EnumerateSessionsAsync().Pages.WithCancellation(CancellationToken.None))
        {
            // Only the continuation request shape is under test.
        }

        await Assert.That(client.Requests.Count).IsEqualTo(2);
        await Assert.That(client.Requests[0]).IsNull();
        await Assert.That(client.Requests[1]?.Cursor).IsEqualTo("cur_1");
        await Assert.That(client.Requests[1]?.Limit).IsNull();
        await Assert.That(client.Requests[1]?.Order).IsNull();
    }

    private static SessionMessageListResponse Page(string? next, params ISessionMessageInfo[] messages) => new()
    {
        Status = 200,
        Messages = messages,
        Cursor = new ListCursor { Next = next, },
    };

    private static SessionListResponse SessionPage(string? next) => new()
    {
        Status = 200,
        Sessions = [],
        Cursor = new ListCursor { Next = next, },
    };

    private static SessionMessageUser Message(string id) => new()
    {
        Id = id,
        Text = id,
        Time = new SessionMessageUserTime { Created = 0, },
    };

    private sealed class StubSessionClient(params SessionMessageListResponse[] pages) : SessionClient
    {
        private readonly Queue<SessionMessageListResponse> _pages = new(pages);

        public List<SessionMessageListRequest?> Requests { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public override Task<SessionMessageListResponse> ListMessagesAsync(SessionMessageListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed class FailingSessionClient(SessionMessageListResponse firstPage, Exception failure) : SessionClient
    {
        private bool _served;

        public override Task<SessionMessageListResponse> ListMessagesAsync(SessionMessageListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                return Task.FromException<SessionMessageListResponse>(failure);
            }

            _served = true;
            return Task.FromResult(firstPage);
        }
    }

    private sealed class StubSessionsClient(params SessionListResponse[] pages) : SessionsClient
    {
        private readonly Queue<SessionListResponse> _pages = new(pages);

        public List<SessionListRequest?> Requests { get; } = [];

        public override Task<SessionListResponse> ListSessionsAsync(SessionListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_pages.Dequeue());
        }
    }
}
