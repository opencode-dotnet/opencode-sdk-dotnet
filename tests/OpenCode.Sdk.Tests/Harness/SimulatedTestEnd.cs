using System.Runtime.ExceptionServices;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness.Abstractions;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

internal sealed class SimulatedTestEnd(IOwnedOperationDeadline deadline, TimeSpan timeout)
{
    public async Task CompleteAsync(ITestEndWork work, bool testFailed, CancellationToken cancellationToken)
    {
        Exception? checkFailure = null;
        try
        {
            await work.CheckNoInvocationLeftPendingAsync();
        }
        catch (Exception exception)
        {
            checkFailure = exception;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var eviction = work.EvictFinishedLocationsAsync(cancellation.Token);
            try
            {
                await deadline.WaitAsync("finished location eviction", eviction, timeout, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                await cancellation.CancelOnWorkerAsync();
                try
                {
                    await deadline.WaitAsync("finished location eviction", eviction, timeout, CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    _ = eviction.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw;
                }

                _ = eviction.Exception;
                throw;
            }

            await eviction;
        }
        catch (Exception exception) when (testFailed || checkFailure is not null)
        {
            work.ReportEvictionFailure(exception);
        }

        if (checkFailure is not null)
        {
            ExceptionDispatchInfo.Capture(checkFailure).Throw();
        }
    }
}
