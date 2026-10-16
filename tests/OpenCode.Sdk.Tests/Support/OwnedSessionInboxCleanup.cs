using System.Globalization;
using System.Runtime.ExceptionServices;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class OwnedSessionInboxCleanup
{
    private readonly IOwnedOperationDeadline _deadline;
    private readonly OwnedCleanup _interruptCleanup;
    private readonly Func<DriveInvocation, OwnedDriveInvocation> _invocationOwner;
    private readonly List<OwnedDriveInvocation> _invocations = [];
    private readonly OwnedCleanup _sessionFinalization;
    private readonly TimeSpan _timeout;
    private OwnedCleanup? _retainedWaitCleanup;

    public OwnedSessionInboxCleanup(
        SessionClient session,
        ExperimentalClient experimental,
        string sessionId,
        DriveController controller,
        TimeSpan timeout)
        : this(
            token => InterruptAsync(session, sessionId, token),
            token => WaitAsync(experimental, sessionId, token),
            token => RemoveAsync(session, sessionId, token),
            invocation => new OwnedDriveInvocation(controller, invocation),
            timeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(experimental);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(controller);
    }

    internal OwnedSessionInboxCleanup(
        Func<CancellationToken, Task> interrupt,
        Func<CancellationToken, Task> wait,
        Func<CancellationToken, Task> remove,
        Func<DriveInvocation, OwnedDriveInvocation> invocationOwner,
        TimeSpan timeout,
        IOwnedOperationDeadline? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(invocationOwner);
        _timeout = timeout;
        _deadline = deadline ?? new OwnedOperationDeadline();
        _invocationOwner = invocationOwner;
        _interruptCleanup = new OwnedCleanup(timeout, _deadline);
        _interruptCleanup.Own("session interrupt", interrupt);
        _sessionFinalization = new OwnedCleanup(timeout, _deadline);
        _sessionFinalization.Own("session wait", wait);
        _sessionFinalization.Own("session removal", remove);
    }

    public OwnedDriveInvocation RetainInvocation(DriveInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var owned = _invocationOwner(invocation);
        _invocations.Add(owned);
        return owned;
    }

    public void RetainWait(Task<ExperimentalSessionWaitResponse> pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (_retainedWaitCleanup is not null)
        {
            throw new InvalidOperationException("A session wait task is already retained.");
        }

        _retainedWaitCleanup = new OwnedCleanup(_timeout, _deadline);
        _retainedWaitCleanup.Own("original session wait", pending);
    }

    public async Task CompleteAsync(Exception? primaryFailure)
    {
        var failure = primaryFailure;
        var sessionCleanupFailed = false;

        failure = await CompleteStageAsync(_interruptCleanup, failure);
        sessionCleanupFailed |= _interruptCleanup.OperationFailures.Count > 0;

        if (_retainedWaitCleanup is { } retainedWaitCleanup)
        {
            failure = await CompleteStageAsync(retainedWaitCleanup, failure);
            sessionCleanupFailed |= retainedWaitCleanup.OperationFailures.Count > 0;
        }

        failure = await CompleteStageAsync(_sessionFinalization, failure);
        sessionCleanupFailed |= _sessionFinalization.OperationFailures.Count > 0;

        if (sessionCleanupFailed)
        {
            var driveCleanup = new OwnedCleanup(_timeout, _deadline);
            foreach (var invocation in _invocations.Where(invocation => !invocation.IsFinished))
            {
                driveCleanup.Own("drive invocation " + invocation.Invocation.Id, invocation.DisconnectAsync);
            }

            failure = await CompleteStageAsync(driveCleanup, failure);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task<Exception?> CompleteStageAsync(OwnedCleanup cleanup, Exception? primaryFailure)
    {
        try
        {
            await cleanup.CompleteAsync(primaryFailure);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task InterruptAsync(
        SessionClient session,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var response = await session.InterruptAsync(
            requestOptions: OpenCodeRequestOptions.NoThrow,
            cancellationToken: cancellationToken);
        RequireResponse(response, sessionId, "interrupt", 200);
    }

    private static async Task WaitAsync(
        ExperimentalClient experimental,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var response = await experimental.WaitForSessionAsync(sessionId, OpenCodeRequestOptions.NoThrow, cancellationToken);
        RequireResponse(response, sessionId, "wait", 204);
    }

    private static async Task RemoveAsync(
        SessionClient session,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var response = await session.RemoveAsync(OpenCodeRequestOptions.NoThrow, cancellationToken);
        RequireResponse(response, sessionId, "removal", 204);
    }

    private static void RequireResponse(
        OpenCodeResponse response,
        string sessionId,
        string operation,
        int successStatus)
    {
        if ((response.Status == successStatus && !response.IsError)
            || (response is { Status: 404, Error: SessionNotFoundError missing }
                && missing.SessionId == sessionId))
        {
            return;
        }

        throw new InvalidOperationException(
            "Owned session " + operation + " returned status " + response.Status.ToString(CultureInfo.InvariantCulture) +
            ", error " + response.Error?.GetType().Name + ", body " + response.RawBody + ".");
    }
}
