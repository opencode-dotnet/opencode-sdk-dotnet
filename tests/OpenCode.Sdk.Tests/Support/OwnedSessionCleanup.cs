using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class OwnedSessionCleanup
{
    internal const string FailuresKey = OwnedCleanup.FailuresKey;
    internal const string LateFailuresKey = OwnedCleanup.LateFailuresKey;

    private readonly Func<CancellationToken, Task> _interrupt;
    private readonly Func<CancellationToken, Task> _remove;
    private readonly OwnedCleanup _cleanup;
    private bool _turnCompleted;

    internal LateCleanupFailureReport? LateFailures =>
        _cleanup.LateFailures;

    public OwnedSessionCleanup(SessionClient session, TimeSpan timeout)
        : this(
            async token =>
            {
                _ = await session.InterruptAsync(cancellationToken: token);
            },
            async token =>
            {
                _ = await session.RemoveAsync(cancellationToken: token);
            },
            timeout)
    {
        ArgumentNullException.ThrowIfNull(session);
    }

    internal OwnedSessionCleanup(
        Func<CancellationToken, Task> interrupt,
        Func<CancellationToken, Task> remove,
        TimeSpan timeout, IOwnedOperationDeadline? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        ArgumentNullException.ThrowIfNull(remove);
        _interrupt = interrupt;
        _remove = remove;
        _cleanup = new OwnedCleanup(timeout, deadline ?? new OwnedOperationDeadline());
    }

    public void MarkTurnCompleted() => _turnCompleted = true;

    public void MarkTurnStarted() => _turnCompleted = false;

    public void Own(string name, Func<CancellationToken, Task> resourceCleanup) =>
        _cleanup.Own(name, resourceCleanup);

    public async Task CompleteAsync(Exception? primaryFailure)
    {
        if (!_turnCompleted)
        {
            _cleanup.Own("session interrupt", _interrupt);
        }

        _cleanup.Own("session removal", _remove);
        await _cleanup.CompleteAsync(primaryFailure);
    }
}
