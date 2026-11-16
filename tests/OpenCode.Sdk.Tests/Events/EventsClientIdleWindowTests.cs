using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using static OpenCode.Sdk.Tests.Events.Support.EventStreamSteps;

namespace OpenCode.Sdk.Tests.Events;

/// <summary>
/// A connected event stream is cut when its idle window passes without a byte. Every byte,
/// including a heartbeat, restarts the window; the source reads independently of its consumers.
/// Every bound runs on the test's clock, and each step waits until the bound it passes is armed.
/// </summary>
public sealed class EventsClientIdleWindowTests
{
    private readonly FixtureLoader _fixtures = new();

    private string SessionCreatedFrame => WireBodyData.Frames(_fixtures.LoadJson("Events.session-created.json"));

    [Test]
    public async Task A_Connected_Stream_Should_Be_Cut_After_45_Seconds_Without_A_Byte()
    {
        using var first = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(number =>
            number is 1 ? ScriptedEventServer.Stream(first) : ScriptedEventServer.StreamOf(ConnectedFrame));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var next = await PullAfterConnectionAsync(events);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(IdleWindow - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        await Assert.That(server.RequestCount).IsEqualTo(1);
        server.Time.Advance(Delay);

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(first.IsDisposed).IsTrue();
    }

    /// <summary>A client that sets its own idle window, as upstream's <c>idleTimeout</c> allows, is cut when that window passes and not one tick before.</summary>
    [Test]
    public async Task A_Configured_Idle_Window_Should_Cut_A_Silent_Stream_Exactly_When_It_Passes()
    {
        var window = TimeSpan.FromSeconds(10);
        using var first = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(
            number => number is 1 ? ScriptedEventServer.Stream(first) : ScriptedEventServer.StreamOf(ConnectedFrame),
            eventStreamIdleTimeout: window);
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(window, HangGuard.Bound);
        server.Time.Advance(window - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(first.IsDisposed).IsTrue();
    }

    [Test]
    public async Task A_Heartbeat_Should_Start_A_Configured_Idle_Window_Again()
    {
        var window = TimeSpan.FromSeconds(10);
        using var body = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body), eventStreamIdleTimeout: window);
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(window, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(9));
        body.Append(ScriptedEventServer.Heartbeat);
        await server.Time.WaitForArmedAsync(window, HangGuard.Bound);
        server.Time.Advance(window - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        body.Append(SessionCreatedFrame);

        await AssertSessionCreatedAsync(await next.WaitAsync(HangGuard.Bound), events, server);
    }

    /// <summary>A heartbeat re-arms the full idle window before the next empty read, without delivering an event.</summary>
    [Test]
    public async Task A_Heartbeat_Should_Arm_A_Full_Idle_Window_Before_The_Next_Empty_Read()
    {
        var window = TimeSpan.FromSeconds(10);
        using var body = new LiveEventBody(ConnectedFrame);
        var firstEmptyRead = body.NextEmptyRead;
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body), eventStreamIdleTimeout: window);
        using var caller = new CancellationTokenSource();
        var events = server.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();
        await WithPendingCancellationAsync(caller, events, async startPull =>
        {
            startPull();
            var observedRead = firstEmptyRead.ContinueWith(static task => task.Status,
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            await Assert.That(await observedRead.WaitAsync(HangGuard.Bound)).IsEqualTo(TaskStatus.RanToCompletion);
            await server.Time.WaitForArmedAsync(window, HangGuard.Bound);
            server.Time.Advance(TimeSpan.FromSeconds(9));

            var afterHeartbeat = body.NextEmptyRead;
            body.Append(ScriptedEventServer.Heartbeat);
            await afterHeartbeat.WaitAsync(HangGuard.Bound);

            await Assert.That(server.Time.ArmedIn(window)).IsEqualTo(1);
        });
    }

    /// <summary>
    /// Before <c>server.connected</c> only the fixed 2 s connect bound runs: bytes that arrive first
    /// neither extend it nor hand the attempt to the idle window, even one shorter than the bound.
    /// </summary>
    [Test]
    public async Task Bytes_Before_Server_Connected_Should_Leave_The_Connect_Bound_In_Charge()
    {
        using var first = new LiveEventBody(ScriptedEventServer.Heartbeat);
        using var server = new ScriptedEventServer(
            number => number is 1 ? ScriptedEventServer.Stream(first) : ScriptedEventServer.StreamOf(ConnectedFrame),
            eventStreamIdleTimeout: TimeSpan.FromSeconds(1));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(ConnectBound, HangGuard.Bound);
        await server.WaitForRequestAsync(1, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(1.5));
        first.Append(ScriptedEventServer.Heartbeat);
        server.Time.Advance(ConnectBound - TimeSpan.FromSeconds(1.5) - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        await Assert.That(server.RequestCount).IsEqualTo(1);
        server.Time.Advance(OneTick);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(first.IsDisposed).IsTrue();
    }

    /// <summary>Any byte counts as activity, even one that does not finish a line, as upstream's <c>onActivity</c> fires on every chunk.</summary>
    [Test]
    public async Task A_Single_Byte_Should_Start_The_Idle_Window_Again()
    {
        using var body = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(30));
        body.Append(":");
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(IdleWindow - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        body.Append(" heartbeat\n\n" + SessionCreatedFrame);

        await AssertSessionCreatedAsync(await next.WaitAsync(HangGuard.Bound), events, server);
    }

    [Test]
    public async Task A_Heartbeat_Should_Keep_A_Quiet_Stream_Open()
    {
        using var body = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(30));
        body.Append(ScriptedEventServer.Heartbeat);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(IdleWindow - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        body.Append(SessionCreatedFrame);

        await AssertSessionCreatedAsync(await next.WaitAsync(HangGuard.Bound), events, server);
    }

    private static async Task AssertSessionCreatedAsync(bool moved, IAsyncEnumerator<IEvent> events, ScriptedEventServer server)
    {
        await Assert.That(moved).IsTrue();
        await Assert.That(events.Current).IsTypeOf<SessionCreated>();
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    /// <summary>The source keeps reading and observing idle silence even when its consumer holds the admission marker.</summary>
    [Test]
    public async Task SubscribeAsync_Should_Keep_The_Idle_Window_Running_While_An_Event_Is_Held()
    {
        using var body = new LiveEventBody(ConnectedFrame);
        using var replacement = new LiveEventBody(ConnectedFrame);
        var reading = body.NextEmptyRead;
        var replacementReading = replacement.NextEmptyRead;
        using var server = new ScriptedEventServer(index => ScriptedEventServer.Stream(index == 1 ? body : replacement));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = await NextAsync(events);
        await Assert.That(first).IsTypeOf<EventServerConnected>();
        await reading.WaitAsync(HangGuard.Bound);
        await Assert.That(server.Time.ArmedIn(IdleWindow)).IsEqualTo(1);
        server.Time.Advance(IdleWindow);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        var reconnecting = server.Events.GetConnectionState();
        await Assert.That(reconnecting.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(reconnecting.Error).IsTypeOf<OpenCodeTransportException>();
        server.Time.Advance(Delay);
        await replacementReading.WaitAsync(HangGuard.Bound);
        await Assert.That(server.Events.GetConnectionState().Status).IsEqualTo(EventConnectionStatus.Connected);
        await Assert.That(body.IsDisposed).IsTrue();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        var next = await NextAsync(events);
        await Assert.That(next).IsTypeOf<EventServerConnected>();
        await Assert.That(next).IsNotSameReferenceAs(first);
    }
}
