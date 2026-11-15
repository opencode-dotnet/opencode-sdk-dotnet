using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Passes a wait's expected delays only after the manual clock has armed each one.</summary>
internal sealed class ManualClockWaits(ManualTimeProvider time)
{
    public async Task<T> ConsumeWaitsAsync<T>(Func<CancellationToken, Task<T>> start, TimeSpan[] waits, CancellationTokenSource caller)
    {
        Task<T>? wait = null;
        Task? passing = null;
        try
        {
            passing = PassWaitsAsync(() => wait = start(caller.Token), waits);
            // This check has no cancellation token; its owner cancels the operation during cleanup.
            await passing.WaitAsync(HangGuard.Bound, CancellationToken.None);
            return await wait!.WaitAsync(HangGuard.Bound, caller.Token);
        }
        finally
        {
            // Cleanup observes both tasks even after their owner has cancelled.
            await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await ObserveAsync(passing, wait);
        }
    }

    public async Task ConsumeWaitsAsync(Func<CancellationToken, Task> start, TimeSpan[] waits, CancellationTokenSource caller)
    {
        _ = await ConsumeWaitsAsync(async token =>
        {
            await start(token);
            return true;
        }, waits, caller);
    }

    public static async Task ObserveAsync(params Task?[] tasks)
    {
        var all = Task.WhenAll(tasks.OfType<Task>());
        // The owners have cancelled; this cleanup has no cancellation token of its own.
        _ = await Task.WhenAny(all).WaitAsync(HangGuard.Bound, CancellationToken.None);
        _ = all.Exception;
    }

    public async Task CancelBetweenPollsAsync(Func<CancellationToken, Task> start, CancellationTokenSource caller)
    {
        var wait = start(caller.Token);
        using var decided = new CancellationTokenSource();
        var created = time.WaitForTimerAsync(HangGuard.Bound, decided.Token);
        try
        {
            var first = await Task.WhenAny(wait, created).WaitAsync(HangGuard.Bound, caller.Token);
            await Assert.That(first).IsSameReferenceAs(created);
            await created;
            await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(1);
            // Cancellation must finish, and the assertion must observe the operation's own token, not a wrapper's.
            await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);

            var failure = await Assert.That(wait.WaitAsync(HangGuard.Bound, CancellationToken.None)).Throws<OperationCanceledException>();
            await Assert.That(failure!.CancellationToken == caller.Token).IsTrue();
            await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(25))).IsEqualTo(0);
        }
        finally
        {
            // Cleanup observes both tasks even after their owners have cancelled.
            await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await caller.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
            await ObserveAsync(created, wait);
        }
    }

    /// <summary>
    /// Starts the operation and passes each wait between its attempts: waits in real time until the
    /// next wait is created or the operation ends, requires the wait, checks its length, and only then
    /// moves the clock. The losing waiter is cancelled and observed. On a failed check, the caller
    /// cancels the operation and observes both it and this helper before the failure propagates.
    /// </summary>
    public async Task PassWaitsAsync(Func<Task> start, TimeSpan[] waits)
    {
        var operation = start();
        using (var decided = new CancellationTokenSource())
        {
            Task? created = null;
            try
            {
                foreach (var expected in waits)
                {
                    created = time.WaitForTimerAsync(HangGuard.Bound, decided.Token);
                    var first = await Task.WhenAny(operation, created).WaitAsync(HangGuard.Bound, decided.Token);
                    await Assert.That(first).IsSameReferenceAs(created);
                    await created;
                    await Assert.That(time.ArmedIn(expected)).IsEqualTo(1);
                    time.Advance(expected);
                }
            }
            finally
            {
                // The timer owner's cancellation ends a losing read; cleanup must still observe it.
                await decided.CancelOnWorkerAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
                if (created is not null)
                {
                    _ = await Task.WhenAny(created).WaitAsync(HangGuard.Bound, CancellationToken.None);
                    _ = created.Exception;
                }
            }
        }
    }
}
