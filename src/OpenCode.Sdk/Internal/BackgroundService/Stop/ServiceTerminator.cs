using System.Globalization;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;
using OpenCode.Sdk.Internal.BackgroundService.ProcessControl;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.Diagnostics;
using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.BackgroundService.Stop;

/// <summary>
/// The pinned client's <c>terminate</c> (<c>effect/service.ts</c>): read the registration and act
/// only while it is still this one, send the terminate rung, poll for the process to leave, send the
/// kill rung when it is still there, poll again, and remove the registration once the process is
/// gone and the record is still this one. Between the rungs the registration is not read again: it
/// can disappear or change hands before the signalled process exits, and only that process tells
/// whether it stopped, so the escalation follows it, while a successor's record is never removed.
/// The poll is the pinned client's own schedule (<c>stopPollInterval</c> × <c>stopPollAttempts</c>,
/// 50 ms and 100 at the pin), and the SDK's addition is what it looks at: the process identity, so
/// the pid is signalled only while the process under it is the one first seen, and a pid that was
/// reused reads as the registered process being gone. The one failure is a process still running
/// after the kill rung; the registration then stays.
/// </summary>
internal sealed class ServiceTerminator(IServiceFileSystem fileSystem, IServiceProcessControl processControl, ServiceTiming timing)
{
    /// <summary>Ends the registered process and removes its registration.</summary>
    /// <param name="registration">The registration as it was read.</param>
    /// <param name="registrationFile">The file it was read from.</param>
    /// <param name="cancellationToken">The caller's token: before a signal it prevents the signal; after one it ends the poll.</param>
    /// <returns>A task that completes when the process is gone, or at once when the registration was no longer this one.</returns>
    /// <exception cref="OpenCodeServerException">The process is still running after the kill rung.</exception>
    public async Task TerminateAsync(ServiceRegistration registration, string registrationFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationFile);

        var expected = ServiceRegistrationIdentity.Of(registration);
        if (!await StillRegisteredAsync(registrationFile, expected, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (processControl.TrySnapshot(registration.ProcessId) is { } target)
        {
            await EndAsync(target, registrationFile, cancellationToken).ConfigureAwait(false);
        }

        if (await StillRegisteredAsync(registrationFile, expected, cancellationToken).ConfigureAwait(false))
        {
            Remove(registrationFile);
        }
    }

    /// <summary>
    /// Both rungs, escalating by the signalled process alone. Completes when the process is gone.
    /// </summary>
    private async Task EndAsync(ProcessIdentity target, string registrationFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = processControl.TrySignal(target, ProcessSignal.Terminate);
        if (await WaitForExitAsync(target, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = processControl.TrySignal(target, ProcessSignal.Kill);
        if (await WaitForExitAsync(target, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        throw new OpenCodeServerException(
            $"The registered background service (pid {target.ProcessId.ToString(CultureInfo.InvariantCulture)}) is still running after the kill rung; its registration at '{registrationFile}' was left in place.");
    }

    /// <summary>
    /// The pinned client's <c>stopped</c> under its poll schedule (<c>effect/service.ts</c>):
    /// one look, then up to <c>stopPollAttempts</c> more, <c>stopPollInterval</c> apart. Each look
    /// reads the process identity, so the process is gone when its identity is — because it
    /// exited, or because the operating system gave its pid to a newer process. The caller's
    /// cancellation ends the poll between looks.
    /// </summary>
    /// <returns>True when the process is gone; false when it is still there at the last look.</returns>
    private async Task<bool> WaitForExitAsync(ProcessIdentity target, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= timing.StopPollAttempts; attempt++)
        {
            if (processControl.TrySnapshot(target.ProcessId) != target)
            {
                return true;
            }

            if (attempt < timing.StopPollAttempts)
            {
                await Task.Delay(timing.StopPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task<bool> StillRegisteredAsync(string registrationFile, ServiceRegistrationIdentity expected, CancellationToken cancellationToken)
    {
        var current = await ServiceRegistrationReader.TryReadAsync(fileSystem, registrationFile, cancellationToken).ConfigureAwait(false);
        return current is not null && ServiceRegistrationIdentity.Of(current) == expected;
    }

    [SlopwatchSuppress(
        "SW003",
        "The pinned client removes the registration under Effect.ignore (effect/service.ts): the process is gone, which is the outcome, and a record left behind is read as absent next time.")]
    private void Remove(string registrationFile)
    {
        try
        {
            _ = fileSystem.TryDelete(registrationFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Effect.ignore on fs.remove.
        }
    }
}
