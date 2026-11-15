using OpenCode.Sdk.Internal;
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
    public async Task SubscribeAsync_Should_Delegate_The_Request_Options_And_Token()
    {
        var time = new ManualTimeProvider();
        var frames = WireBodyData.Frames(ScriptedEventServer.Connected,
            _fixtures.LoadJson("Events.session-created.json"), _fixtures.LoadJson("Events.unknown-log-event.json"));
        using var handler = new RecordingHttpHandler(_ => ScriptedEventServer.StreamOf(frames));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector { Directory = "/ambient" }, time: time);
        var door = new EventsClient(pipeline);
        var request = new EventSubscribeRequest { Types = [new EventType("session.created"), new EventType("rpc.sdk test/ç")] };
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
            .IsEquivalentTo(["?types=session.created&types=rpc.sdk%20test%2F%C3%A7"], CollectionOrdering.Matching);
        await Assert.That(handler.Requests.Select(static attempt => attempt.Headers["x-opencode-directory"]))
            .IsEquivalentTo(["%2Foverride%2F%C3%A7"], CollectionOrdering.Matching);

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
