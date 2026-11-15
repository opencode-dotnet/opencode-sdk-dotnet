using OpenCode.Sdk.Internal;
using System.Text.Json;
using System.Threading.Channels;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

public sealed class EventsClientTests
{
    private readonly FixtureLoader _fixtures = new();

    [Test]
    public async Task SubscribeAsync_Should_Receive_An_Event_Published_During_The_Refresh_After_Admission()
    {
        using var body = new LiveEventBody(EventStreamSteps.ConnectedFrame);
        var initiallyReading = body.NextEmptyRead;
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        await using var observer = server.Events.SubscribeAsync(new EventSubscribeRequest { Types = [new("rpc.other")] }).GetAsyncEnumerator();
        await Assert.That((await EventStreamSteps.NextAsync(observer)).Type).IsEqualTo("server.connected");
        await using var original = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That((await EventStreamSteps.NextAsync(original)).Type).IsEqualTo("server.connected");
        await initiallyReading.WaitAsync(HangGuard.Bound);
        var overflowFrame = WireBodyData.Frames(JsonSerializer.Serialize(SharedEventFeedScenario.Event("overflow"), OpenCodeJsonContext.Default.IEvent));
        var drained = body.NextEmptyRead;
        body.Append(string.Concat(Enumerable.Repeat(overflowFrame, 4097)));
        await drained.WaitAsync(HangGuard.Bound);
        _ = await Assert.That(async () => await original.MoveNextAsync()).Throws<OpenCodeEventSubscriberOverflowException>();

        await using var replacement = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That((await EventStreamSteps.NextAsync(replacement)).Type).IsEqualTo("server.connected");
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Channel.CreateBounded<bool>(1);
        async Task<Dictionary<string, int>> RefreshAsync()
        {
            refreshEntered.SetResult();
            _ = await release.Reader.ReadAsync(CancellationToken.None);
            return new Dictionary<string, int>(StringComparer.Ordinal) { ["E2"] = 1 };
        }

        var refreshing = RefreshAsync();
        try
        {
            await refreshEntered.Task.WaitAsync(HangGuard.Bound);
            drained = body.NextEmptyRead;
            body.Append(WireBodyData.Frames(JsonSerializer.Serialize(SharedEventFeedScenario.Event("E2"), OpenCodeJsonContext.Default.IEvent)));
            await drained.WaitAsync(HangGuard.Bound);
        }
        finally
        {
            _ = release.Writer.TryWrite(true);
        }

        var projection = await refreshing.WaitAsync(HangGuard.Bound);
        var tail = await EventStreamSteps.NextAsync(replacement);
        await Assert.That(tail.Id).IsEqualTo("E2");
        var applied = projection.TryAdd(tail.Id!, 1);
        await Assert.That(applied).IsFalse();
        await Assert.That(projection.Keys).IsEquivalentTo(["E2"], CollectionOrdering.Matching);
        await Assert.That(projection["E2"]).IsEqualTo(1);
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task SubscribeAsync_Should_Snapshot_Types_And_Validate_Default_Values_At_The_Call()
    {
        var frames = WireBodyData.Frames(ScriptedEventServer.Connected,
            _fixtures.LoadJson("Events.session-created.json"), _fixtures.LoadJson("Events.unknown-log-event.json"));
        using var handler = new RecordingHttpHandler(_ => ScriptedEventServer.StreamOf(frames));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, time: new ManualTimeProvider());
        var door = new EventsClient(pipeline);
        _ = await Assert.That(() => door.SubscribeAsync(new EventSubscribeRequest { Types = [default] })).Throws<ArgumentException>();
        await Assert.That(handler.Requests).IsEmpty();
        var types = new List<EventType> { EventType.SessionCreated };
        var recipe = door.SubscribeAsync(new EventSubscribeRequest { Types = types });
        types.Clear();
        types.Add(new EventType("session.invented.tomorrow"));
        await using var events = recipe.GetAsyncEnumerator();

        await Assert.That((await EventStreamSteps.NextAsync(events)).Type).IsEqualTo("server.connected");
        await Assert.That((await EventStreamSteps.NextAsync(events)).Id).IsEqualTo("evt_1");
        await Assert.That(handler.Requests.Single().RequestUri!.Query).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task SubscribeAsync_Should_Share_One_Source_Between_Two_Subscriptions()
    {
        var bodies = new List<LiveEventBody>();
        using var server = new ScriptedEventServer(_ =>
        {
            var body = new LiveEventBody(EventStreamSteps.ConnectedFrame);
            bodies.Add(body);
            return ScriptedEventServer.Stream(body);
        });
        await using var first = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await using var second = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var firstMarker = await EventStreamSteps.NextAsync(first);
        var secondMarker = await EventStreamSteps.NextAsync(second);

        await Assert.That(server.RequestCount).IsEqualTo(1);
        await Assert.That(secondMarker).IsSameReferenceAs(firstMarker);
        bodies.Single().Append(WireBodyData.Frames(_fixtures.LoadJson("Events.session-created.json")));
        var left = await EventStreamSteps.NextAsync(first);
        var right = await EventStreamSteps.NextAsync(second);
        await Assert.That(left.Id).IsEqualTo("evt_1");
        await Assert.That(right).IsSameReferenceAs(left);
        await first.DisposeAsync();
        await Assert.That(bodies.Single().IsDisposed).IsFalse();
        bodies.Single().Append(WireBodyData.Frames(_fixtures.LoadJson("Events.unknown-log-event.json")));
        var tail = (UnknownEvent)await EventStreamSteps.NextAsync(second);
        await Assert.That(tail.Payload.GetProperty("id").GetString()).IsEqualTo("evt_3");
    }

    [Test]
    public async Task SubscribeAsync_Should_Delegate_The_Request_Options_And_Token()
    {
        var time = new ManualTimeProvider();
        var frames = WireBodyData.Frames(ScriptedEventServer.Connected,
            _fixtures.LoadJson("Events.session-created.json"), _fixtures.LoadJson("Events.unknown-log-event.json"));
        using var handler = new RecordingHttpHandler(_ => ScriptedEventServer.StreamOf(frames));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector { Directory = "/ambient" }, time: time);
        var door = new EventsClient(pipeline);
        var request = new EventSubscribeRequest
        {
            Types = [new EventType("session.created"), new EventType("rpc.sdk test/ç"), new EventType("session.invented.tomorrow")],
        };
        var options = new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/override/ç" } };
        using var caller = new CancellationTokenSource();
        await using var events = door.SubscribeAsync(request, options, caller.Token).GetAsyncEnumerator();

        var received = new List<IEvent>();
        for (var index = 0; index < 3; index++)
        {
            await Assert.That(await events.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound)).IsTrue();
            received.Add(events.Current);
        }

        await Assert.That(received.Select(static item => item.Type))
            .IsEquivalentTo(["server.connected", "session.created", "session.invented.tomorrow"], CollectionOrdering.Matching);
        string?[] expectedIds = ["evt_c", "evt_1", null];
        await Assert.That(received.Select(static item => item.Id)).IsEquivalentTo(expectedIds, CollectionOrdering.Matching);
        await Assert.That(((UnknownEvent)received[2]).Payload.GetProperty("id").GetString()).IsEqualTo("evt_3");
        await Assert.That(handler.Requests.Select(static attempt => attempt.RequestUri!.Query))
            .IsEquivalentTo([string.Empty], CollectionOrdering.Matching);
        await Assert.That(handler.Requests.Select(static attempt => attempt.Headers["x-opencode-directory"]))
            .IsEquivalentTo(["%2Fambient"], CollectionOrdering.Matching);

        using var readyHandler = EventStreamSteps.CreateReconnectHandler();
        using var readyHttpClient = new HttpClient(readyHandler);
        using var readyPipeline = PipelineFactory.Create(readyHttpClient, time: time);
        await caller.CancelOnWorkerAsync();
        await using var cancelled = new EventsClient(readyPipeline).SubscribeAsync(request, options, caller.Token).GetAsyncEnumerator();
        var exception = await EventStreamSteps.PendingCancellationAsync(cancelled).WaitAsync(HangGuard.Bound);
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task SubscribeAsync_Should_Preserve_The_Protected_Mocking_Seam()
    {
        var door = new MockEventsClient();
        await using var events = door.SubscribeAsync().GetAsyncEnumerator();
        var received = new List<IEvent>();
        while (await events.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound))
        {
            received.Add(events.Current);
        }

        await Assert.That(received).IsEquivalentTo([MockEventsClient.KnownEvent], CollectionOrdering.Matching);
        var exception = await Assert.That(() => new UnoverriddenEventsClient().SubscribeAsync()).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).IsEqualTo(
            "'EventsClient.Pipeline' was invoked on an instance created through the protected mocking constructor; override the member under test.");
    }

    private sealed class MockEventsClient : EventsClient
    {
        internal static readonly IEvent KnownEvent = new EventServerConnected { Id = "mocked", Data = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) };

        public override IAsyncEnumerable<IEvent> SubscribeAsync(EventSubscribeRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) => Sequence();

        private static async IAsyncEnumerable<IEvent> Sequence()
        {
            await Task.CompletedTask;
            yield return KnownEvent;
        }
    }

    private sealed class UnoverriddenEventsClient : EventsClient
    {
    }
}
