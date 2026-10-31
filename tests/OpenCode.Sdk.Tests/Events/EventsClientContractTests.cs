using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

/// <summary>
/// The live bus keeps a representative runtime corpus: one shared durable/live leaf and one
/// unknown tag. Every connection starts with <c>server.connected</c>, and the stream reconnects
/// instead of ending, so each test reads the events it expects and stops; the declared errors and
/// the failure channel surface after the reconnect minute (<see cref="EventsClientReconnectTests"/>).
/// The clock is the test's, so no reconnect bound can pass while a test reads; every read is
/// bounded by a hang guard instead. Generator tests own structural breadth.
/// </summary>
public sealed class EventsClientContractTests
{
    [Test]
    public async Task SubscribeAsync_Should_Send_Types_On_Every_Reconnect_Attempt()
    {
        var time = new ManualTimeProvider();
        using var handler = EventStreamSteps.CreateReconnectHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, time: time);
        var request = new EventSubscribeRequest
        {
            Types = [new EventType(EventType.Values.SessionCreated), new EventType("rpc.sdk-test")],
        };
        await using var events = new EventsClient(pipeline).SubscribeAsync(request).GetAsyncEnumerator();

        var reconnect = await EventStreamSteps.PullAfterConnectionAsync(events);
        await time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        time.Advance(EventStreamSteps.Delay);
        await Assert.That(await reconnect.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests.Select(static attempt => attempt.RequestUri!.Query))
            .IsEquivalentTo(["?types=session.created&types=rpc.sdk-test", "?types=session.created&types=rpc.sdk-test"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SubscribeAsync_Should_Omit_The_Query_Without_Types(int kind)
    {
        using var scenario = ContractScenario.RespondingWithFrames(EventStreamSteps.ConnectedFrame);
        var request = kind switch
        {
            0 => null,
            1 => new EventSubscribeRequest(),
            _ => new EventSubscribeRequest { Types = [] },
        };
        await using var events = scenario.Client.Events.SubscribeAsync(request).GetAsyncEnumerator();

        await Assert.That(await EventStreamSteps.NextAsync(events)).IsTypeOf<EventServerConnected>();
        await Assert.That(scenario.Requests.Single().RequestUri!.Query).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SubscribeAsync_Should_Type_A_Shared_Durable_Leaf()
    {
        using var scenario = RespondingWithEvents(WireBodyData.SessionCreatedEvent);

        var items = await CollectAsync(scenario, count: 1);

        var created = (SessionCreated)items.Single();
        await Assert.That(created).IsAssignableTo<IEvent>();
        await Assert.That(created).IsAssignableTo<ISessionEventDurable>();
        await Assert.That(created.Data.SessionId).IsEqualTo("ses_9");
        await Assert.That(scenario.RequestCount).IsEqualTo(1);
        await Assert.That(scenario.LastRequest.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(scenario.LastRequest.Address).IsEqualTo(new Uri("http://localhost:4096/api/event"));
    }

    /// <summary>A session-scoped event names its session through the common contract, so a consumer routes without switching on its type first.</summary>
    [Test]
    public async Task SubscribeAsync_Should_Route_A_Session_Scoped_Event_By_Its_Session()
    {
        using var scenario = RespondingWithEvents(WireBodyData.SessionCreatedEvent, WireBodyData.UnknownLogEvent);

        var items = await CollectAsync(scenario, count: 2);

        await Assert.That(items[0] is ISessionScopedEvent { SessionId: "ses_9" }).IsTrue();
        await Assert.That(items[1]).IsNotAssignableTo<ISessionScopedEvent>();
    }

    [Test]
    public async Task SubscribeAsync_Should_Preserve_An_Unknown_Event_Tag()
    {
        using var scenario = RespondingWithEvents(WireBodyData.UnknownLogEvent);

        var items = await CollectAsync(scenario, count: 1);

        var unknown = (UnknownEvent)items.Single();
        await Assert.That(unknown.Type).IsEqualTo("session.invented.tomorrow");
        await Assert.That(unknown.Payload.GetProperty("id").GetString()).IsEqualTo("evt_3");
    }

    /// <summary>
    /// An enum value the SDK does not know no longer fails its frame, so the subscription keeps
    /// reading: the frame after it arrives too.
    /// </summary>
    [Test]
    public async Task SubscribeAsync_Should_Continue_Past_An_Event_With_An_Unknown_Enum_Value()
    {
        using var scenario = RespondingWithEvents(WireBodyData.UnknownReasonInterruptedEvent, WireBodyData.SessionCreatedEvent);

        var items = await CollectAsync(scenario, count: 2);

        await Assert.That(items).Count().IsEqualTo(2);
        var interrupted = (SessionExecutionInterrupted)items[0];
        await Assert.That(interrupted.Data.Reason.IsKnown).IsFalse();
        await Assert.That(items[1]).IsTypeOf<SessionCreated>();
    }

    [Test]
    public async Task SubscribeAsync_Should_Start_With_Server_Connected()
    {
        using var scenario = RespondingWithEvents();

        await using var events = scenario.Events.SubscribeAsync().GetAsyncEnumerator();

        await Assert.That(await events.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
    }

    [Test]
    public async Task SubscribeAsync_Should_Preserve_An_Unreadable_Known_Event_And_Its_Neighbour()
    {
        var bad = WireBodyData.EventWithoutData("session.created");
        using var scenario = RespondingWithEvents(bad, WireBodyData.SessionDeletedEvent);
        using var caller = new CancellationTokenSource();
        using var waiterOwner = new CancellationTokenSource();
        var events = scenario.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();
        Task<OperationCanceledException?>? pending = null;
        Task? reconnectDelay = null;
        try
        {
            await Assert.That(await EventStreamSteps.NextAsync(events)).IsTypeOf<EventServerConnected>();
            pending = EventStreamSteps.PendingCancellationAsync(events);
            reconnectDelay = scenario.Time.WaitForArmedOrCanceledAsync(EventStreamSteps.Delay, HangGuard.Bound, waiterOwner.Token);
            var winner = await Task.WhenAny(pending, reconnectDelay).WaitAsync(HangGuard.Bound);
            await Assert.That(winner).IsNotSameReferenceAs(reconnectDelay);
            await Assert.That(scenario.Time.ArmedIn(EventStreamSteps.Delay)).IsEqualTo(0);
            await Assert.That(await pending).IsNull();
            await Assert.That(events.Current).IsTypeOf<UnknownEvent>();
            var unknown = (UnknownEvent)events.Current;
            await Assert.That(unknown.Type).IsEqualTo("session.created");
            await Assert.That(unknown.Payload.GetRawText()).IsEqualTo(bad);
            await Assert.That(await EventStreamSteps.NextAsync(events)).IsTypeOf<SessionDeleted>();
            await Assert.That(scenario.RequestCount).IsEqualTo(1);
            await Assert.That(scenario.Time.ArmedIn(EventStreamSteps.Delay)).IsEqualTo(0);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            await waiterOwner.CancelOnWorkerAsync();
            // Cleanup observes both race participants even after their owners have cancelled.
            try
            {
                if (pending is not null)
                {
                    _ = await pending.WaitAsync(HangGuard.Bound, CancellationToken.None);
                }
            }
            finally
            {
                try
                {
                    if (reconnectDelay is not null)
                    {
                        // The waiter's owner cancels it once the race is decided; anything but that cancellation propagates.
                        var ended = await Task.WhenAny(reconnectDelay).WaitAsync(HangGuard.Bound, CancellationToken.None);
                        if (!ended.IsCanceled || !waiterOwner.IsCancellationRequested)
                        {
                            await ended;
                        }
                    }
                }
                finally
                {
                    await events.DisposeAsync().AsTask().WaitAsync(HangGuard.Bound, CancellationToken.None);
                }
            }
        }
    }

    /// <summary>Answers every subscribe with <c>server.connected</c> and then the given events, as the server writes them.</summary>
    private static ScriptedEventServer RespondingWithEvents(params string[] events) =>
        new(_ => ScriptedEventServer.StreamOf(WireBodyData.Frames([ScriptedEventServer.Connected, .. events])));

    /// <summary>Reads the events after <c>server.connected</c> until <paramref name="count"/> arrived, then stops reading.</summary>
    private static async Task<List<IEvent>> CollectAsync(ScriptedEventServer scenario, int count)
    {
        var items = new List<IEvent>();
        await using var events = scenario.Events.SubscribeAsync().GetAsyncEnumerator();
        while (items.Count < count)
        {
            if (!await events.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound))
            {
                throw new InvalidOperationException("The event stream ended.");
            }

            if (events.Current is not EventServerConnected)
            {
                items.Add(events.Current);
            }
        }

        return items;
    }
}
