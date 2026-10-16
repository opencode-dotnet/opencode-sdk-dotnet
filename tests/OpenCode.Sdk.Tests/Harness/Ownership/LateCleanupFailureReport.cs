using System.Collections.Concurrent;

namespace OpenCode.Sdk.Tests.Harness.Ownership;

internal sealed class LateCleanupFailureReport
{
    private ConcurrentQueue<KeyValuePair<string, Exception>> _failures = new();
    private readonly TaskCompletionSource<KeyValuePair<string, Exception>> _firstFailure = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _registrations;
    private readonly List<Task> _observers = [];

    public bool HasRegistrations => Volatile.Read(ref _registrations) > 0;

    public IReadOnlyCollection<KeyValuePair<string, Exception>> DiagnosticFailures => _failures;

    public IReadOnlyList<KeyValuePair<string, Exception>> Failures => [.. _failures];

    public void InheritDiagnostics(Exception? primaryFailure)
    {
        if (primaryFailure?.Data[OwnedCleanup.LateFailuresKey]
            is ConcurrentQueue<KeyValuePair<string, Exception>> previous)
        {
            // Called before registering work. Nested owners append to the same attached queue.
            _failures = previous;
        }
    }

    public void Observe(string name, Task operation, Func<OperationCanceledException, bool>? expectedCancellation = null)
    {
        _ = Interlocked.Increment(ref _registrations);
        _observers.Add(ObserveCompletionAsync(name, operation, expectedCancellation));
    }

    private async Task ObserveCompletionAsync(string name, Task operation,
        Func<OperationCanceledException, bool>? expectedCancellation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            var failures = operation.Exception?.Flatten().InnerExceptions.AsEnumerable() ?? [exception];
            foreach (var failure in failures)
            {
                if (failure is OperationCanceledException cancelled && expectedCancellation?.Invoke(cancelled) is true)
                {
                    continue;
                }

                var entry = new KeyValuePair<string, Exception>(name, failure);
                _failures.Enqueue(entry);
                _ = _firstFailure.TrySetResult(entry);
            }
        }
    }

    public async Task<KeyValuePair<string, Exception>> WaitForFailureAsync(CancellationToken cancellationToken) =>
        await _firstFailure.Task.WaitAsync(cancellationToken);

    public async Task WaitForAllAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(_observers).WaitAsync(cancellationToken);
}
