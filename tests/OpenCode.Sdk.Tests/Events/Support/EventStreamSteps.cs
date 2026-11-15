using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Events.Support;

/// <summary>
/// Event stream tests share the pinned connection bounds and steps for pulling events and
/// observing cancellation before releasing an enumerator.
/// </summary>
internal sealed class EventStreamSteps
{
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(2);

    internal static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(45);

    internal static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    internal static readonly string ConnectedFrame = WireBodyData.Frames(ScriptedEventServer.Connected);

    internal static RecordingHttpHandler CreateReconnectHandler() =>
        new(static _ => ScriptedEventServer.StreamOf(ConnectedFrame, ends: true));

    internal static async Task<Task<bool>> PullAfterConnectionAsync(IAsyncEnumerator<IEvent> events)
    {
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
        return PendingNext(events);
    }

    internal static async Task WithPendingCancellationAsync(CancellationTokenSource caller, IAsyncEnumerator<IEvent> events,
        Func<Action, Task> whilePending)
    {
        Task<OperationCanceledException?>? pending = null;
        try
        {
            await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
            await whilePending(() => pending = PendingCancellationAsync(events));
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            // Cleanup must observe completion even after the caller has cancelled.
            try
            {
                if (pending is not null)
                {
                    _ = await pending.WaitAsync(HangGuard.Bound, CancellationToken.None);
                }
            }
            finally
            {
                await events.DisposeAsync().AsTask().WaitAsync(HangGuard.Bound, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Asks for the next event without waiting for it, and keeps the cancellation it ends with whole:
    /// a downlevel <c>WaitAsync</c> rethrows a cancellation without its token.
    /// </summary>
    internal static async Task<OperationCanceledException?> PendingCancellationAsync<T>(IAsyncEnumerator<T> events)
    {
        try
        {
            _ = await events.MoveNextAsync();
            return null;
        }
        catch (OperationCanceledException exception)
        {
            return exception;
        }
    }

    /// <summary>Asks for the next event without waiting for it, so the test can move time while the read is pending.</summary>
    internal static Task<bool> PendingNext<T>(IAsyncEnumerator<T> events) => events.MoveNextAsync().AsTask();

    internal static async Task ObserveSourceCompletionAsync(EventSource source, bool cancellation = false)
    {
        var task = cancellation ? source.CancellationObservation! : source.RunnerObservation!;
        var observation = task.ContinueWith(static completed => completed.Status,
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        await Assert.That(await observation.WaitAsync(HangGuard.Bound, CancellationToken.None)).IsEqualTo(TaskStatus.RanToCompletion);
    }

    internal static async Task<IEvent> SharedEventAsync(IAsyncEnumerator<IEvent> first, IAsyncEnumerator<IEvent> second, string id)
    {
        var left = await NextAsync(first);
        var right = await NextAsync(second);
        await Assert.That(left.Id).IsEqualTo(id);
        await Assert.That(right).IsSameReferenceAs(left);
        return right;
    }

    internal static async Task AssertConnectedAsync(IAsyncEnumerator<IEvent> events, bool moved)
    {
        await Assert.That(moved).IsTrue();
        await Assert.That(events.Current.Type).IsEqualTo("server.connected");
    }

    internal static async Task AssertStateAsync(EventConnectionState state, EventConnectionStatus status, int attempt, Exception? error)
    {
        await Assert.That(state.Status).IsEqualTo(status);
        await Assert.That(state.Attempt).IsEqualTo(attempt);
        await Assert.That(state.Error).IsSameReferenceAs(error);
    }

    internal static async Task CancelAndCaptureAsync<T>(CancellationTokenSource caller, IAsyncEnumerator<T> events)
    {
        await caller.CancelOnWorkerAsync();
        var canceled = await PendingCancellationAsync(events).WaitAsync(HangGuard.Bound, CancellationToken.None);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
    }

    internal static async Task<T> NextAsync<T>(IAsyncEnumerator<T> events)
    {
        if (!await events.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound))
        {
            throw new InvalidOperationException("The event stream ended.");
        }

        return events.Current;
    }
}
