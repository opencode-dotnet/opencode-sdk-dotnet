using System.Runtime.CompilerServices;
using System.Text.Json;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Rpc.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Rpc;

public sealed class EventsClientRpcExtensionsTests
{
    private const string FirstNonce = "n1";
    private const string SecondNonce = "n2";
    private const string OtherType = "rpc.opencode.sdk.test.rpc.other";

    [Test]
    public async Task SubscribeAsync_Should_Subscribe_Lazily_With_No_Selection_Or_Options_And_The_Caller_Token()
    {
        using var caller = new CancellationTokenSource();
        var client = Door(new DoorStream([Target()]));

        var stream = client.SubscribeAsync(RpcFacadeData.Observed, caller.Token);
        await Assert.That(client.ReceivedCalls()).IsEmpty();
        await using var events = stream.GetAsyncEnumerator();
        await Assert.That(client.ReceivedCalls()).IsEmpty();
        await Assert.That(await events.MoveNextAsync()).IsTrue();

        var call = client.ReceivedCalls().Single();
        await Assert.That(call.GetMethodInfo().Name).IsEqualTo(nameof(EventsClient.SubscribeAsync));
        var arguments = call.GetArguments();
        await Assert.That(arguments[0]).IsNull();
        await Assert.That(arguments[1]).IsNull();
        await Assert.That(arguments[2]).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task SubscribeAsync_Should_Select_Only_Exact_Event_Types_And_Preserve_Envelope_Identity()
    {
        var first = RpcFacadeData.ObservedEvent(RpcFacadeData.ObservedType, FirstNonce);
        var second = RpcFacadeData.ObservedEvent(RpcFacadeData.ObservedType, SecondNonce);
        var stream = new DoorStream([
            new EventServerConnected { Id = "marker", Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal) },
            RpcFacadeData.ObservedEvent(OtherType, FirstNonce), first,
            RpcFacadeData.ObservedEvent("rpc.opencode.sdk.test.other.observed", FirstNonce),
            RpcFacadeData.ObservedEvent("rpc.OPENCODE.SDK.TEST.RPC.observed", FirstNonce),
            RpcFacadeData.ObservedEvent("rpc.opencode.sdk.test.rpc.nested.observed", FirstNonce),
            RpcFacadeData.ObservedEvent("rpc.opencode.sdk.test.rpc.observed.extra", FirstNonce), second,
        ]);
        var client = Door(stream);
        var received = new List<RpcEventPayload<RpcNonce>>();

        await foreach (var payload in client.SubscribeAsync(RpcFacadeData.Observed))
        {
            received.Add(payload);
        }

        await Assert.That(received.Select(static payload => payload.Data.Nonce))
            .IsEquivalentTo([FirstNonce, SecondNonce], CollectionOrdering.Matching);
        await Assert.That(received[0].Event).IsSameReferenceAs(first);
        await Assert.That(received[1].Event).IsSameReferenceAs(second);
    }

    private static EventsClient Door(DoorStream stream)
    {
        var client = Substitute.For<EventsClient>();
        client.SubscribeAsync(null, null, Arg.Any<CancellationToken>())
            .Returns(call => stream.ReadAsync(call.Arg<CancellationToken>()));
        return client;
    }

    [Test]
    public async Task SubscribeAsync_Should_Propagate_The_Same_Cancellation_Exception_And_Dispose_The_Door()
    {
        using var caller = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(caller.Token);
        var stream = new DoorStream([Target(), Target(SecondNonce)], async (index, token) =>
        {
            if (index == 1)
            {
                await caller.CancelOnWorkerAsync();
                throw token == caller.Token ? cancellation : new OperationCanceledException(token);
            }
        });

        var failure = await Assert.That(() => DrainAsync(Door(stream).SubscribeAsync(RpcFacadeData.Observed, caller.Token)))
            .Throws<OperationCanceledException>();

        await Assert.That(failure).IsSameReferenceAs(cancellation);
        await Assert.That(stream.Finished).IsTrue();
    }

    [Test]
    public async Task SubscribeAsync_Should_Dispose_The_Door_On_Leaving_Without_Reading_Ahead()
    {
        var stream = new DoorStream([Target(), Target(SecondNonce)]);

        await using (var events = Door(stream).SubscribeAsync(RpcFacadeData.Observed).GetAsyncEnumerator())
        {
            await Assert.That(await events.MoveNextAsync()).IsTrue();
            await Assert.That(events.Current.Data.Nonce).IsEqualTo(FirstNonce);
            await Assert.That(stream.Pulled).IsEqualTo(1);
            await Assert.That(stream.Finished).IsFalse();
        }

        await Assert.That(stream.Finished).IsTrue();
    }

    [Test]
    [Arguments("mismatch")]
    [Arguments("null")]
    public async Task SubscribeAsync_Should_Throw_A_Transport_Failure_And_Dispose_When_Data_Is_Unreadable(string key)
    {
        var stream = new DoorStream([key == "mismatch"
            ? RpcFacadeData.UnreadableEvent(FirstNonce) : RpcFacadeData.ObservedEvent(RpcFacadeData.ObservedType, FirstNonce)]);
        var descriptor = key == "mismatch" ? RpcFacadeData.Observed : RpcFacadeData.NullObserved;

        var failure = await Assert.That(() => DrainAsync(Door(stream).SubscribeAsync(descriptor)))
            .ThrowsExactly<OpenCodeTransportException>();

        await Assert.That(failure?.Message)
            .IsEqualTo("The data of RPC event 'rpc.opencode.sdk.test.rpc.observed' does not match its data type.");
        await Assert.That(failure?.InnerException).IsTypeOf<JsonException>();
        await Assert.That(stream.Finished).IsTrue();
    }

    [Test]
    public async Task SubscribeAsync_Should_Propagate_The_Same_Door_Failure()
    {
        var overflow = new OpenCodeEventSubscriberOverflowException();
        var stream = new DoorStream([Target()], (_, _) => throw overflow);

        var failure = await Assert.That(() => DrainAsync(Door(stream).SubscribeAsync(RpcFacadeData.Observed)))
            .ThrowsExactly<OpenCodeEventSubscriberOverflowException>();

        await Assert.That(failure).IsSameReferenceAs(overflow);
    }

    [Test]
    [Arguments(true, false, "client")]
    [Arguments(false, true, "rpcEvent")]
    [Arguments(true, true, "client")]
    public async Task SubscribeAsync_Should_Guard_Arguments_In_Order_At_The_Call(bool nullClient, bool nullEvent, string parameter)
    {
        var stream = new DoorStream([Target()]);
        var client = Door(stream);
        // Null-forgiving arguments deliberately cross the public guards with invalid inputs.
        var failure = await Assert.That(() => EventsClientRpcExtensions.SubscribeAsync(
            nullClient ? null! : client, nullEvent ? null! : RpcFacadeData.Observed)).ThrowsExactly<ArgumentNullException>();

        await Assert.That(failure?.ParamName).IsEqualTo(parameter);
        await Assert.That(client.ReceivedCalls()).IsEmpty();
        await Assert.That(stream.Pulled).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribeAsync_Should_Read_The_Real_Door_Without_A_Type_Query_And_Preserve_Cancellation()
    {
        var observedEventFrame = new FixtureLoader().LoadJson("Rpc.observed-event.json");
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.StreamOf(
            WireBodyData.Frames(ScriptedEventServer.Connected, RpcFacadeData.OtherEventFrame, observedEventFrame)));
        using var caller = new CancellationTokenSource();
        var events = server.Events.SubscribeAsync(RpcFacadeData.Observed, caller.Token).GetAsyncEnumerator();
        var pending = Task.CompletedTask;
        try
        {
            var next = EventStreamSteps.PendingNext(events);
            pending = next;
            await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(events.Current.Event.Id).IsEqualTo(RpcFacadeData.EventId);
            await Assert.That(events.Current.Event.Location.Directory).IsEqualTo(RpcFacadeData.Directory);
            await Assert.That(events.Current.Data.Nonce).IsEqualTo(RpcFacadeData.Nonce);
            await Assert.That(server.LastRequest.Address?.Query).IsEqualTo(string.Empty);
            var cancellation = EventStreamSteps.PendingCancellationAsync(events);
            pending = cancellation;
            await caller.CancelOnWorkerAsync();
            var failure = await cancellation.WaitAsync(HangGuard.Bound, CancellationToken.None);
            await Assert.That(failure?.CancellationToken).IsEqualTo(caller.Token);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            try
            {
                // Observe completion without replacing a failed wait with cancellation.
                _ = await pending.ContinueWith(static completed => completed.Status,
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                    .WaitAsync(HangGuard.Bound, CancellationToken.None);
            }
            finally
            {
                await events.DisposeAsync().AsTask().WaitAsync(HangGuard.Bound, CancellationToken.None);
            }
        }
    }

    [Test]
    [Arguments("method")]
    [Arguments("enumeration")]
    public async Task SubscribeAsync_Should_Observe_Either_Distinct_Entry_Token(string entry)
    {
        using var method = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        await (entry == "method" ? method : enumeration).CancelOnWorkerAsync();
        var stream = new DoorStream([Target()]);

        await Assert.That(() => DrainAsync(Door(stream).SubscribeAsync(RpcFacadeData.Observed, method.Token), enumeration.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(stream.Finished).IsTrue();
    }

    [Test]
    public async Task SubscribeAsync_Should_Propagate_A_Non_Json_Event_Metadata_Failure()
    {
        var stream = new DoorStream([Target()]);

        var failure = await Assert.That(() => DrainAsync(Door(stream).SubscribeAsync(RpcFacadeData.FailingObserved)))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(failure).IsSameReferenceAs(RpcFacadeData.MetadataFailure);
        await Assert.That(stream.Finished).IsTrue();
    }

    private static EventRpc Target(string nonce = FirstNonce) => RpcFacadeData.ObservedEvent(RpcFacadeData.ObservedType, nonce);

    private static async Task DrainAsync(IAsyncEnumerable<RpcEventPayload<RpcNonce>> stream, CancellationToken cancellationToken = default)
    {
        await foreach (var payload in stream.WithCancellation(cancellationToken))
        {
            _ = payload.Data;
        }
    }

    private sealed class DoorStream(IReadOnlyList<IEvent> items, Func<int, CancellationToken, Task>? before = null)
    {
        public int Pulled { get; private set; }
        public bool Finished { get; private set; }

        public async IAsyncEnumerable<IEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await Task.CompletedTask;
                foreach (var item in items)
                {
                    if (before is not null)
                    {
                        await before(Pulled, cancellationToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Pulled++;
                    yield return item;
                }
            }
            finally
            {
                Finished = true;
            }
        }
    }
}
