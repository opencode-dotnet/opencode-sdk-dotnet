using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class OwnedEventReader
{
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _window;
    private readonly CancellationToken _eventToken;
    private readonly OwnedCleanup _cleanup;
    private bool _teardownCancellation;
    private Task _reader = Task.CompletedTask;
    private Task _cancellation = Task.CompletedTask;

    public OwnedEventReader(TimeSpan observationWindow, TimeSpan cleanupTimeout, CancellationToken callerToken,
        IOwnedOperationDeadline? deadline = null)
    {
        _callerToken = callerToken;
        _window = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _eventToken = _window.Token;
        _window.CancelAfter(observationWindow);
        _cleanup = new OwnedCleanup(cleanupTimeout, deadline ?? new OwnedOperationDeadline());
    }

    public CancellationToken Token => _eventToken;

    /// <summary>
    /// Whether the caller's own token fired. A wait that ends this way is a cancellation and keeps
    /// that identity; only the reader's own window expiring is a timeout.
    /// </summary>
    public bool CallerCancellationRequested => _callerToken.IsCancellationRequested;

    public LateCleanupFailureReport? LateFailures => _cleanup.LateFailures;

    public bool IsTeardownCancellation(OperationCanceledException exception) =>
        _teardownCancellation && !_callerToken.IsCancellationRequested
        && exception.CancellationToken == Token && Token.IsCancellationRequested;

    public void Own(Task reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async Task CompleteAsync(Exception? primaryFailure)
    {
        _teardownCancellation = !_window.IsCancellationRequested;
        // Already on a worker: downlevel CancelAsync would queue another worker and spin waiting for it.
        _cancellation = Task.Run(_window.Cancel, CancellationToken.None);
        _cleanup.Own("event cancellation", _cancellation);
        _cleanup.Own("event reader", _reader, IsTeardownCancellation);
        try
        {
            await _cleanup.CompleteAsync(primaryFailure);
        }
        finally
        {
            // Retain the window while either callbacks or reader disposal still use it.
            _ = Task.WhenAll(_cancellation, _reader).ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    _window.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
