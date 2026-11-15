namespace OpenCode.Sdk.Internal;

/// <summary>Owns physical attempts independently of consumer pulls; cleanup failures escape the attempt boundary.</summary>
internal sealed class EventSourceRunner(IEventAttemptSource attempts, TimeProvider time, TimeSpan idleWindow)
{
    private readonly TimeSpan _idleWindow = idleWindow;
    public async Task RunAsync(SharedEventFeed feed, EventSource source)
    {
        while (feed.IsCurrent(source))
        {
            source.Token.ThrowIfCancellationRequested();
            await ReadAttemptAsync(feed, source).ConfigureAwait(false);
            feed.RecordDrop(source);
#if NET
            await Task.Delay(StreamReconnect.Delay, time, source.Token).ConfigureAwait(false);
#else
            await time.Delay(StreamReconnect.Delay, source.Token).ConfigureAwait(false);
#endif
        }
    }

    private async Task ReadAttemptAsync(SharedEventFeed feed, EventSource source)
    {
        using var attempt = new StreamAttempt(time, _idleWindow, source.Token);
        var events = attempts.OpenAsync(attempt, attempt.Token).GetAsyncEnumerator(attempt.Token);
        await using var enumeration = events.ConfigureAwait(false);
        while (true)
        {
            bool first;
            try
            {
                if (!await events.MoveNextAsync().ConfigureAwait(false))
                {
                    return;
                }

                first = !attempt.IsEstablished;
                if (first)
                {
                    attempt.Establish();
                }
            }
            catch (Exception) when (!source.Token.IsCancellationRequested)
            {
                // An ordinary physical-attempt failure retries; enumeration cleanup remains outside this boundary.
                return;
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
}
