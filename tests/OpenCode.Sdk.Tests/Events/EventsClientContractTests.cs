using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

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
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

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

        await Assert.That(await events.MoveNextAsync().AsTask().WaitAsync(HangGuard)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
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
            if (!await events.MoveNextAsync().AsTask().WaitAsync(HangGuard))
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
