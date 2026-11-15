namespace OpenCode.Sdk.Internal;

/// <summary>Owns physical attempts independently of consumer pulls; cleanup failures escape the attempt boundary.</summary>
internal sealed class EventSourceRunner(IEventAttemptSource attempts, TimeProvider time, TimeSpan idleWindow)
{
    private readonly TimeSpan _idleWindow = idleWindow;
    public async Task RunAsync(SharedEventFeed feed, EventSource source)
    {
        var counter = 0;
        while (feed.RecordAttemptStarted(source))
        {
            source.Token.ThrowIfCancellationRequested();
            var result = await ReadAttemptAsync(feed, source).ConfigureAwait(false);
            if (result.ConnectedFor >= TimeSpan.FromSeconds(1))
            {
                counter = 0;
            }

            counter++;
            feed.RecordDrop(source, result.Failure, counter);
#if NET
            await Task.Delay(StreamReconnect.Delay, time, source.Token).ConfigureAwait(false);
#else
            await time.Delay(StreamReconnect.Delay, source.Token).ConfigureAwait(false);
#endif
        }
    }

    private async Task<AttemptResult> ReadAttemptAsync(SharedEventFeed feed, EventSource source)
    {
        using var attempt = new StreamAttempt(time, _idleWindow, source.Token);
        var events = attempts.OpenAsync(attempt, attempt.Token).GetAsyncEnumerator(attempt.Token);
        await using var enumeration = events.ConfigureAwait(false);
        long? connectedAt = null;
        AttemptResult End(Exception failure) => new(failure,
            connectedAt is { } timestamp ? time.GetElapsedTime(timestamp) : TimeSpan.Zero);
        while (true)
        {
            bool first;
            try
            {
                if (!await events.MoveNextAsync().ConfigureAwait(false))
                {
                    return End(FailureClassification.EventStreamEnded());
                }

                first = !attempt.IsEstablished;
                if (first)
                {
                    attempt.Establish();
                    connectedAt = time.GetTimestamp();
                }
            }
            catch (OperationCanceledException exception) when (!source.Token.IsCancellationRequested)
            {
                return End(attempt.Describe(exception));
            }
            catch (Exception exception) when (!source.Token.IsCancellationRequested)
            {
                // Cleanup remains outside this boundary. Only a failure of this physical attempt retries.
                return End(exception);
            }

            if (first)
            {
                feed.RecordConnected(source, events.Current);
            }
            else
            {
                feed.Publish(source, events.Current);
            }
        }
    }

    private sealed record AttemptResult(Exception Failure, TimeSpan ConnectedFor);
}
