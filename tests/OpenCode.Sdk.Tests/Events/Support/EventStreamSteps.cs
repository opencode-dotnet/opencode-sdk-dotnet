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
    internal static async Task<OperationCanceledException?> PendingCancellationAsync(IAsyncEnumerator<IEvent> events)
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
    internal static Task<bool> PendingNext(IAsyncEnumerator<IEvent> events) => events.MoveNextAsync().AsTask();

    internal static async Task<IEvent> NextAsync(IAsyncEnumerator<IEvent> events)
    {
        if (!await events.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound))
        {
            throw new InvalidOperationException("The event stream ended.");
        }

        return events.Current;
    }
}
