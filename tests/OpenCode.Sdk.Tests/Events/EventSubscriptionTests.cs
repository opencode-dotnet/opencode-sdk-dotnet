using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using NSubstitute;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

public sealed class EventSubscriptionTests
{
    [Test]
    [Arguments("fault")]
    [Arguments("root")]
    [Arguments("false")]
    [Arguments("overflow")]
    public async Task MoveNextAsync_Should_Report_Cancellation_After_Overflow_Or_A_Stored_Ending(string ending)
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M", caller.Token);
        var failure = new InvalidOperationException("source cleanup");
        switch (ending)
        {
            case "fault":
                await scenario.FaultAsync(failure);
                var observed = await Assert.That(async () => await events.MoveNextAsync()).Throws<InvalidOperationException>();
                await Assert.That(observed).IsSameReferenceAs(failure);
                break;
            case "root":
                scenario.Feed.Dispose();
                _ = await Assert.That(async () => await events.MoveNextAsync()).Throws<ObjectDisposedException>();
                break;
            case "false":
                await events.DisposeAsync();
                await Assert.That(await events.MoveNextAsync()).IsFalse();
                break;
            default:
                for (var index = 0; index < 4097; index++)
                {
                    await scenario.PublishAsync("overflow");
                }

                _ = await Assert.That(async () => await events.MoveNextAsync()).Throws<OpenCodeEventSubscriberOverflowException>();
                break;
        }

        await caller.CancelOnWorkerAsync();
        var canceled = await EventStreamSteps.PendingCancellationAsync(events).WaitAsync(HangGuard.Bound);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(events.RetainedCount).IsEqualTo(0);
    }

    [Test]
    public async Task MoveNextAsync_Should_Release_The_First_Lease_When_The_Second_Token_Registers_Cancelled()
    {
        using var caller = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var events = new EventSubscription<IEvent>(scenario.Feed, EventSelection.Capture(null), caller.Token, enumeration.Token);
        events.Precheck();
        scenario.Feed.Adopt(events, events.RegisterToken(caller.Token));
        await Assert.That(events.RegistrationCount).IsEqualTo(1);
        await enumeration.CancelOnWorkerAsync();
        scenario.Feed.Adopt(events, events.RegisterToken(enumeration.Token));

        await Assert.That(events.RegistrationCount).IsEqualTo(0);
        var completion = new TaskCompletionSource<Delivery<IEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Feed.WithGate(work => events.MoveLocked(new EventPendingMove<IEvent>(completion), work));
        var result = await completion.Task;
        await Assert.That(result.Error).IsTypeOf<OperationCanceledException>();
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MoveNextAsync_Should_Release_Registrations_When_Cancellation_Runs_Inline(bool disposeBeforeAdoption)
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var events = new EventSubscription<IEvent>(scenario.Feed, EventSelection.Capture(null), caller.Token, default);
        events.Precheck();
        if (!disposeBeforeAdoption)
        {
            await caller.CancelOnWorkerAsync();
        }

        var lease = events.RegisterToken(caller.Token);
        if (disposeBeforeAdoption)
        {
            await events.DisposeAsync();
        }

        scenario.Feed.Adopt(events, lease);
        await caller.CancelOnWorkerAsync();
        var canceled = await EventStreamSteps.PendingCancellationAsync(events).WaitAsync(HangGuard.Bound);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(events.RegistrationCount).IsEqualTo(0);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task MoveNextAsync_Should_Observe_Both_Entry_Tokens_And_Preserve_One_Tokens_Identity(bool sameToken, bool cancelEnumerator)
    {
        using var caller = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        var enumeratorToken = sameToken ? caller.Token : enumeration.Token;
        await using var scenario = new SharedEventFeedScenario();
        await using var events = (EventSubscription<IEvent>)scenario.Feed.Subscribe(EventSelection.Capture(null), caller.Token)
            .GetAsyncEnumerator(enumeratorToken);
        var moving = EventStreamSteps.PendingNext(events);
        var attempt = await scenario.Attempts.NextOpenAsync();
        await attempt.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Id).IsEqualTo("M");
        await Assert.That(events.RegistrationCount).IsEqualTo(sameToken ? 1 : 2);
        await (cancelEnumerator && !sameToken ? enumeration : caller).CancelOnWorkerAsync();

        await Assert.That(events.RegistrationCount).IsEqualTo(0);
        var canceled = await EventStreamSteps.PendingCancellationAsync(events).WaitAsync(HangGuard.Bound);
        await Assert.That(canceled).IsNotNull();
        if (sameToken)
        {
            await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        }
    }

    [Test]
    public async Task MoveNextAsync_Should_Keep_A_Publication_That_Precedes_Cancellation()
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M", caller.Token);
        var moving = EventStreamSteps.PendingNext(events);
        await scenario.PublishAsync("E1");
        await caller.CancelOnWorkerAsync();

        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Id).IsEqualTo("E1");
        var canceled = await EventStreamSteps.PendingCancellationAsync(events).WaitAsync(HangGuard.Bound);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task MoveNextAsync_Should_Keep_4096_Queued_Selected_Events_In_Order()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        string?[] expected = [.. Enumerable.Range(1, 4096).Select(static index =>
            "E" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        foreach (var id in expected)
        {
            await scenario.PublishAsync(id!);
        }

        var received = new List<string?>();
        for (var index = 0; index < 4096; index++)
        {
            received.Add((await EventStreamSteps.NextAsync(events)).Id);
        }

        await Assert.That(received).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(events.RetainedCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MoveNextAsync_Should_Not_Count_A_Pending_Handoff_As_A_Queue_Slot(bool replay)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var owner = await scenario.AdmitAsync("M");
        await using var events = replay
            ? (EventSubscription<IEvent>)scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator()
            : owner;
        var pending = events.MoveNextAsync().AsTask();
        if (!replay)
        {
            await scenario.PublishAsync("E0");
        }

        for (var index = 1; index <= 4097; index++)
        {
            await scenario.PublishAsync("E" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await Assert.That(await pending.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Id).IsEqualTo(replay ? "M" : "E0");
        _ = await Assert.That(async () => await events.MoveNextAsync()).Throws<OpenCodeEventSubscriberOverflowException>();
        await Assert.That(events.RetainedCount).IsEqualTo(0);
    }

    [Test]
    public async Task MoveNextAsync_Should_Check_Live_Cancellation_Before_A_Publication_Handoff()
    {
        using var caller = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = Substitute.For<IEventFeedGateProbe>();
        probe.When(static item => item.OnPublication()).Do(_ =>
        {
            entered.SetResult();
            if (!release.Wait(HangGuard.Bound))
            {
                throw new TimeoutException("The test did not release publication.");
            }
        });
        await using var scenario = new SharedEventFeedScenario(probe);
        await using var events = await scenario.AdmitAsync("M", caller.Token);
        var pending = EventStreamSteps.PendingCancellationAsync(events);
        Task? canceling = null;
        var publishing = scenario.PublishAsync("E1");
        try
        {
            await entered.Task.WaitAsync(HangGuard.Bound);
            canceling = DedicatedThread.Run(caller.Cancel);
            var visible = DedicatedThread.Run(() => caller.Token.WaitHandle.WaitOne(HangGuard.Bound));
            await Assert.That(await visible.WaitAsync(HangGuard.Bound)).IsTrue();
        }
        finally
        {
            release.Set();
            await publishing.WaitAsync(HangGuard.Bound);
            if (canceling is not null)
            {
                await canceling.WaitAsync(HangGuard.Bound);
            }
        }

        var canceled = await pending.WaitAsync(HangGuard.Bound);
        await Assert.That(canceled).IsNotNull();
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(events.RegistrationCount).IsEqualTo(0);
    }

    [Test]
    public async Task MoveNextAsync_Should_Report_Cancellation_Before_Disposal_At_Admission()
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var events = new EventSubscription<IEvent>(scenario.Feed, EventSelection.Capture(null), caller.Token, default);
        events.Precheck();
        await caller.CancelOnWorkerAsync();
        var lease = events.RegisterToken(caller.Token);
        scenario.Feed.Dispose();
        scenario.Feed.Adopt(events, lease);
        var completion = new TaskCompletionSource<Delivery<IEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var move = new EventPendingMove<IEvent>(completion);
        scenario.Feed.WithGate(work => events.MoveLocked(move, work));
        var result = await completion.Task;

        await Assert.That(result.Error).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)result.Error!).CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(events.RegistrationCount).IsEqualTo(0);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("publication")]
    [Arguments("root")]
    [Arguments("pending")]
    public async Task DisposeAsync_Should_Keep_A_Move_Settled_By_Root_Disposal_Or_Publication(string outcome)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var pending = EventStreamSteps.PendingNext(events);
        Task<bool> concurrent;
        try
        {
            concurrent = EventStreamSteps.PendingNext(events);
        }
        catch (InvalidOperationException exception)
        {
            concurrent = Task.FromException<bool>(exception);
        }
        if (string.Equals(outcome, "publication", StringComparison.Ordinal))
        {
            await scenario.PublishAsync("E1");
        }
        else if (string.Equals(outcome, "root", StringComparison.Ordinal))
        {
            scenario.Feed.Dispose();
        }

        await events.DisposeAsync();
        _ = await Assert.That(concurrent).Throws<InvalidOperationException>();
        if (string.Equals(outcome, "root", StringComparison.Ordinal))
        {
            _ = await Assert.That(pending).Throws<ObjectDisposedException>();
        }
        else
        {
            await Assert.That(await pending.WaitAsync(HangGuard.Bound))
                .IsEqualTo(string.Equals(outcome, "publication", StringComparison.Ordinal));
            if (string.Equals(outcome, "publication", StringComparison.Ordinal))
            {
                await Assert.That(events.Current.Id).IsEqualTo("E1");
            }
        }
    }

    [Test]
    public async Task MoveNextAsync_Should_Deliver_Queued_Events_Before_The_Same_Source_Fault()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var first = await scenario.AdmitAsync("M");
        await using var second = await scenario.AdmitAsync("M");
        await scenario.PublishAsync("E1");
        await scenario.PublishAsync("E2");
        var failure = new InvalidOperationException("source cleanup");
        await scenario.FaultAsync(failure);
        foreach (var events in new[] { first, second })
        {
            string?[] actual = [(await EventStreamSteps.NextAsync(events)).Id, (await EventStreamSteps.NextAsync(events)).Id];
            await Assert.That(actual).IsEquivalentTo((string?[])["E1", "E2"], CollectionOrdering.Matching);
            var observed = await Assert.That(async () => await events.MoveNextAsync()).Throws<InvalidOperationException>();
            await Assert.That(observed).IsSameReferenceAs(failure);
            var repeated = await Assert.That(async () => await events.MoveNextAsync()).Throws<InvalidOperationException>();
            await Assert.That(repeated).IsSameReferenceAs(failure);
        }

        await using var fresh = await scenario.AdmitAsync("M2");
        await Assert.That(fresh.Current.Id).IsEqualTo("M2");
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(2);
    }

    [Test]
    public async Task MoveNextAsync_Should_Discard_A_Preserved_Backlog_When_The_Token_Is_Cancelled()
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M", caller.Token);
        await scenario.PublishAsync("E1");
        await scenario.PublishAsync("E2");
        await scenario.FaultAsync(new InvalidOperationException("source cleanup"));
        await caller.CancelOnWorkerAsync();

        var canceled = await EventStreamSteps.PendingCancellationAsync(events).WaitAsync(HangGuard.Bound);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(events.RetainedCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("overflow")]
    [Arguments("fault")]
    [Arguments("root")]
    [Arguments("false")]
    public async Task DisposeAsync_Should_Drain_The_Backlog_And_End_Later_Pulls(string ending)
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M", caller.Token);
        await scenario.PublishAsync("E1");
        await scenario.PublishAsync("E2");
        switch (ending)
        {
            case "fault":
                await scenario.FaultAsync(new InvalidOperationException("source cleanup"));
                break;
            case "root":
                scenario.Feed.Dispose();
                break;
            case "overflow":
                for (var index = 0; index < 4095; index++)
                {
                    await scenario.PublishAsync("overflow");
                }

                break;
            default:
                await events.DisposeAsync();
                break;
        }

        await events.DisposeAsync();
        await Assert.That(events.RetainedCount).IsEqualTo(0);
        await Assert.That(await events.MoveNextAsync()).IsFalse();
        await caller.CancelOnWorkerAsync();
        var canceled = await EventStreamSteps.PendingCancellationAsync(events).WaitAsync(HangGuard.Bound);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
    }

    [Test]
    public async Task MoveNextAsync_Should_Preserve_A_Stored_Fault_Prefix_After_Root_Disposal()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var ended = await scenario.AdmitAsync("M");
        await scenario.PublishAsync("E1");
        await scenario.PublishAsync("E2");
        var failure = new InvalidOperationException("source cleanup");
        await scenario.FaultAsync(failure);
        await using var live = await scenario.AdmitAsync("M2");
        await scenario.PublishAsync("discarded");
        scenario.Feed.Dispose();

        string?[] prefix = [(await EventStreamSteps.NextAsync(ended)).Id, (await EventStreamSteps.NextAsync(ended)).Id];
        await Assert.That(prefix).IsEquivalentTo((string?[])["E1", "E2"], CollectionOrdering.Matching);
        var observed = await Assert.That(async () => await ended.MoveNextAsync()).Throws<InvalidOperationException>();
        await Assert.That(observed).IsSameReferenceAs(failure);
        _ = await Assert.That(async () => await live.MoveNextAsync()).Throws<ObjectDisposedException>();
        await Assert.That(live.RetainedCount).IsEqualTo(0);
    }

    [Test]
    public async Task Capacity_Should_Report_The_Sdk_Bound_For_Consumer_Constructed_Exceptions()
    {
        var cause = new InvalidOperationException("underlying failure");
        OpenCodeEventSubscriberOverflowException[] exceptions = [new(), new("message"), new("message", cause)];

        await Assert.That(exceptions.Select(static exception => exception.Capacity))
            .IsEquivalentTo([4096, 4096, 4096], CollectionOrdering.Matching);
        await Assert.That(exceptions[1].Message).IsEqualTo("message");
        await Assert.That(exceptions[2].Message).IsEqualTo("message");
        await Assert.That(exceptions[2].InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    public async Task MoveNextAsync_Should_Overflow_On_The_4097th_Queued_Selected_Event_But_Not_The_4096th()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = scenario.Feed.Subscribe(EventSelection.Capture(null), CancellationToken.None).GetAsyncEnumerator();
        var first = events.MoveNextAsync().AsTask();
        var attempt = await scenario.Attempts.NextOpenAsync();
        await attempt.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await first.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Id).IsEqualTo("M");
        for (var index = 1; index <= 4096; index++)
        {
            await attempt.PublishAsync(SharedEventFeedScenario.Event("E" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        await attempt.PublishAsync(SharedEventFeedScenario.Event("E4097"));
        var overflow = await Assert.That(async () => await events.MoveNextAsync()).Throws<OpenCodeEventSubscriberOverflowException>();
        await Assert.That(overflow!.Capacity).IsEqualTo(4096);
        await Assert.That(overflow.Message).IsEqualTo("Event subscriber exceeded its 4096-event capacity");
        var repeated = await Assert.That(async () => await events.MoveNextAsync()).Throws<OpenCodeEventSubscriberOverflowException>();
        await Assert.That(repeated).IsSameReferenceAs(overflow);
    }

    [Test]
    public async Task Settle_Should_Keep_The_First_Result_And_Refuse_A_Second_Settlement()
    {
        var completion = new TaskCompletionSource<Delivery<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var move = new EventPendingMove<string>(completion);

        bool[] settlements = [move.Settle(new Delivery<string>("first", null)), move.Settle(new Delivery<string>("second", null))];

        await Assert.That(settlements).IsEquivalentTo([true, false], CollectionOrdering.Matching);
        var delivery = await completion.Task;
        await Assert.That(delivery.Item).IsEqualTo("first");
        await Assert.That(delivery.Error).IsNull();
    }

    [Test]
    public async Task AfterGate_Should_Run_Every_Item_And_Aggregate_The_Gate_Failure_First()
    {
        var gateFailure = new InvalidOperationException("gate");
        var cleanupFailure = new InvalidOperationException("cleanup");
        var ledger = new List<string>();
        var work = new AfterGate();

        var exception = await Assert.That(() => work.Run(() =>
        {
            work.Add(() =>
            {
                ledger.Add("cleanup");
                throw cleanupFailure;
            });
            work.Add(() => ledger.Add("stop"));
            throw gateFailure;
        })).Throws<AggregateException>();

        Exception[] expectedFailures = [gateFailure, cleanupFailure];
        await Assert.That(exception!.InnerExceptions).IsEquivalentTo(expectedFailures, CollectionOrdering.Matching);
        await Assert.That(ledger).IsEquivalentTo(["cleanup", "stop"], CollectionOrdering.Matching);
    }
}
