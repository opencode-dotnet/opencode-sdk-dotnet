using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace OpenCode.Sdk.Internal;

internal sealed class ReconnectingEventStream(TimeProvider time, TimeSpan idleWindow,
    ClientLifetime lifetime, StreamPlane streamPlane)
{
    private readonly ClientLifetime _lifetime = lifetime;

    /// <summary>
    /// Runs one connection after another for a reconnecting operation, as the pinned client does.
    /// Each attempt runs under its own window: 2 seconds to answer and deliver
    /// <c>server.connected</c> first, then the client's idle window without a byte once connected,
    /// stopped while the consumer holds an event. Any failure is followed by a new attempt one
    /// second later. The give-up window runs from the first attempt and again from each drop, stops
    /// once an attempt connects, and when it passes it cuts whatever is in flight and throws the
    /// last failure. The caller's token and a disposed client end the stream at once.
    /// </summary>
    public async IAsyncEnumerable<TPayload> ExecuteReconnectingStreamAsync<TPayload, TCause>(OperationRequest request, IStreamAdapter<TPayload, TCause> adapter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var giveUp = new ProgressWindow(StreamReconnect.GiveUp, time, linked.Token);
        giveUp.Restart();
        ExceptionDispatchInfo? lastFailure = null;
        while (true)
        {
            _lifetime.ThrowIfDisposed();
            var established = false;
            Exception failure;
            var frames = ExecuteAttemptAsync(request, adapter, giveUp, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (frames.ConfigureAwait(false))
            {
                while (true)
                {
                    try
                    {
                        if (!await frames.MoveNextAsync().ConfigureAwait(false))
                        {
                            throw FailureClassification.EventStreamEnded();
                        }
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        break;
                    }

                    // An attempt yields only once established: its first event was server.connected.
                    established = true;
                    yield return frames.Current;
                }
            }

            lastFailure = EndAttempt(failure, lastFailure, giveUp, cancellationToken);
            if (established)
            {
                giveUp.Restart();
            }

            await WaitToReconnectAsync(giveUp, lastFailure, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One connection of a reconnecting stream under its own window (<see cref="StreamAttempt"/>):
    /// it fails unless its first event is <c>server.connected</c>, stops the give-up window once it
    /// is, and ends when its connection ends. A window of its own passing surfaces as a transport
    /// failure; the give-up window and the caller's token pass through as cancellation.
    /// </summary>
    private async IAsyncEnumerable<TPayload> ExecuteAttemptAsync<TPayload, TCause>(OperationRequest request,
        IStreamAdapter<TPayload, TCause> adapter, ProgressWindow giveUp,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCause : IReadOnlyList<Models.IOpenCodeStreamFailureCause>
    {
        using var attempt = new StreamAttempt(time, idleWindow, giveUp.Token);
        var frames = streamPlane.ExecuteStreamCoreAsync(request, adapter, attempt, attempt.Token)
            .GetAsyncEnumerator(cancellationToken);
        await using var enumeration = frames.ConfigureAwait(false);
        while (true)
        {
            bool moved;
            try
            {
                moved = await frames.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!giveUp.IsCancellationRequested)
            {
                throw attempt.Describe(exception);
            }

            if (!moved)
            {
                yield break;
            }

            if (!attempt.IsEstablished)
            {
                attempt.Establish();
                giveUp.Stop();
            }

            // The consumer's time with an event is not the server's silence.
            attempt.Pause();
            yield return frames.Current;
            attempt.Resume();
        }
    }

    /// <summary>
    /// Ends the stream when the attempt's failure ends it: the caller cancelled, the client was
    /// disposed, or the give-up window passed. A give-up throws the attempt's own failure when the
    /// attempt failed on its own, and the failure before it when the give-up cut it. Otherwise the
    /// failure is recorded as the last one.
    /// </summary>
    private ExceptionDispatchInfo EndAttempt(Exception failure, ExceptionDispatchInfo? lastFailure, ProgressWindow giveUp,
        CancellationToken cancellationToken)
    {
        ThrowIfInterrupted(failure, cancellationToken);
        if (giveUp.IsCancellationRequested)
        {
            if (failure is not OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            lastFailure?.Throw();
            throw FailureClassification.EventStreamGiveUp(failure, StreamReconnect.GiveUp);
        }

        return ExceptionDispatchInfo.Capture(failure);
    }

    /// <summary>
    /// Waits before the next attempt; the give-up window passing during the wait throws the last
    /// failure, and a disposed client ends the wait at once.
    /// </summary>
    private async Task WaitToReconnectAsync(ProgressWindow giveUp, ExceptionDispatchInfo lastFailure, CancellationToken cancellationToken)
    {
        try
        {
#if NET
            await Task.Delay(StreamReconnect.Delay, time, giveUp.Token).ConfigureAwait(false);
#else
            await time.Delay(StreamReconnect.Delay, giveUp.Token).ConfigureAwait(false);
#endif
        }
        catch (OperationCanceledException canceled)
        {
            ThrowIfInterrupted(canceled, cancellationToken);
            lastFailure.Throw();
        }
    }

    /// <summary>Reads live caller cancellation before the root client's disposed state.</summary>
    private void ThrowIfInterrupted(Exception failure, CancellationToken caller)
    {
        if (caller.IsCancellationRequested)
        {
            throw FailureClassification.CallerCancellation(failure,
                FailureClassification.CanceledMessage(FailurePhase.EventStreamRead), caller);
        }

        _lifetime.ThrowIfDisposed();
    }
}
