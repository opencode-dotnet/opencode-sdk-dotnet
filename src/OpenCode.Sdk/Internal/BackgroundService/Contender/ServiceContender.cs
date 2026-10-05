using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.Posix;
using static OpenCode.Sdk.Internal.BackgroundService.ProcessControl.BackgroundServiceInterop;
using static OpenCode.Sdk.Internal.BackgroundService.ProcessControl.BackgroundServiceInterop.Kernel32;
using static OpenCode.Sdk.Internal.Posix.PosixInterop;

namespace OpenCode.Sdk.Internal.BackgroundService.Contender;

/// <summary>
/// One detached Ensure contender: the pid the spawn reported, finished/failure observation in the
/// shape of the pinned client's <c>close</c>/<c>error</c> events, and the final 8 KiB of stderr.
/// Finished means what Node's <c>close</c> means: the stderr pipe reached end-of-stream and the
/// process exit was observed, so the exit code is known whenever the election reads it. From the
/// spawn on, the contender watches for its exit on a backoff with no parked thread, beside the
/// drain rather than after it — the pipe closes a moment before the process becomes reapable, and
/// a grandchild can hold it open long after — and on Unix it reaps the pid itself, even after
/// disposal, because the host that spawned it is the only one that can. The contender never
/// kills: <see cref="Release"/> stops retention while the drain keeps consuming, and disposal
/// closes handles without signalling. Either way a released contender may still win the election
/// it was started for.
/// </summary>
internal sealed class ServiceContender : IServiceContender
{
    /// <summary>The pinned client's <c>stderrLimit</c> (<c>service-contender.ts</c>): 8 KiB.</summary>
    private const int StderrLimit = 8 * 1024;

    /// <summary>What a redacted handoff ticket reads as in diagnostics.</summary>
    private const string RedactedMarker = "[redacted]";

    /// <summary>Windows' still-running sentinel for <c>GetExitCodeProcess</c>.</summary>
    private const uint StillActive = 259;

    /// <summary>Return immediately rather than wait: the observation poll never blocks.</summary>
    private const int WaitNoHang = 1;

    /// <summary>The <c>waitpid</c> errno when the pid is not a live child of this process.</summary>
    private const int NoChild = 10;

    /// <summary>The exit watch's first wait; each miss doubles it up to <see cref="ExitWatchCap"/>.</summary>
    private static readonly TimeSpan ExitWatchStart = TimeSpan.FromMilliseconds(1);

    /// <summary>The exit watch's slowest cadence, the one a long-lived service settles on.</summary>
    private static readonly TimeSpan ExitWatchCap = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Stream _stderr;
    private readonly SafeProcessHandle? _process;
    private readonly string? _redact;
    private byte[] _tail = [];

    private bool _released;
    private bool _endOfStderr;
    private bool _disposed;
    private Exception? _error;

    /// <summary>How the process ended, once a poll learned it; unknown when another reaper took the pid first.</summary>
    private ChildExitStatus? _exit;

    public ServiceContender(int processId, Stream stderr, SafeProcessHandle? process, string? redact)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        ProcessId = processId;
        _stderr = stderr;
        _process = process;
        _redact = string.IsNullOrEmpty(redact) ? null : redact;

        // Hot until the first read suspends: the drain owns no caller, only the pipe, and every
        // fault it can see is folded into the error slot — nothing escapes unobserved.
        _ = ObserveAsync();
    }

    /// <summary>Gets the spawned pid. For a Windows batch shim this is the cmd.exe host, never the server: the election reads the service pid from the registration.</summary>
    public int ProcessId { get; }

    /// <inheritdoc />
    public bool Finished
    {
        get
        {
            lock (_gate)
            {
                PollExitLocked();
                return _error is not null || (_endOfStderr && ExitObservedLocked());
            }
        }
    }

    /// <inheritdoc />
    public bool ExitedZero
    {
        get
        {
            lock (_gate)
            {
                PollExitLocked();
                return _exit?.ExitCode == 0;
            }
        }
    }

    /// <summary>
    /// Gets the retained stderr tail as text: the final 8 KiB decoded as UTF-8, trimmed the way
    /// upstream trims, with the handoff ticket redacted when the spawn carried one. Empty once
    /// <see cref="Release"/> gave the retention up.
    /// </summary>
    public string Stderr
    {
        get
        {
            lock (_gate)
            {
                return RenderStderrLocked();
            }
        }
    }

    /// <summary>
    /// The pinned client's <c>contenderFailure</c>: the observation error as-is, else a nonzero
    /// exit or a terminating signal wrapped with the retained stderr tail. Null while the
    /// contender runs, when it exited 0, and when another reaper took the pid before its status
    /// could be read — upstream reports no failure when neither a code nor a signal is known.
    /// </summary>
    /// <returns>The failure, or null when there is none to report.</returns>
    public OpenCodeServerException? TryGetFailure()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_error is not null)
            {
                return new OpenCodeServerException(
                    $"The background service contender (pid {ProcessId.ToString(CultureInfo.InvariantCulture)}) failed to start.",
                    _error);
            }

            PollExitLocked();
            if (_exit is { } exit && (exit.Signal is not null || exit.ExitCode is not (null or 0)))
            {
                return new OpenCodeServerException(WithStderrLocked(
                    $"The background service contender (pid {ProcessId.ToString(CultureInfo.InvariantCulture)}) {exit.Describe()}."));
            }

            return null;
        }
    }

    /// <summary>
    /// Gives the retention up the way upstream's <c>release</c> does: the tail is dropped and
    /// nothing further is kept, but the background drain keeps consuming until the pipe closes so
    /// a full pipe can never wedge the contender. Never signals or kills.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            _released = true;
            _tail = [];
        }
    }

    /// <summary>
    /// Closes the pipe and the process handle without signalling or killing. On Unix the exit
    /// watch outlives disposal until it reaps the pid; on Windows there is nothing to reap.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Under the gate: the exit poll reads this handle only under the gate, so it never
            // sees one closing beneath it.
            _disposed = true;
            _process?.Dispose();
        }

        _stderr.Dispose();
    }

    /// <summary>
    /// Reads the retained tail as one string in arrival order. Truncation can split a UTF-8
    /// sequence at the 8 KiB edge, which decodes the way a streaming decode does — with
    /// replacement characters, the same compromise upstream's buffer-to-string makes.
    /// </summary>
    private string RenderStderrLocked()
    {
        if (_released || _tail.Length == 0)
        {
            return string.Empty;
        }

        var text = Encoding.UTF8.GetString(_tail);
        if (_redact is { Length: > 0 } ticket)
        {
            text = RedactTicket(text, ticket);
        }

        return text.Trim();
    }

    /// <summary>
    /// Ordinal ticket redaction without the framework's comparison overload, which the downlevel
    /// targets do not carry: split at every occurrence and rejoin over the marker.
    /// </summary>
    private static string RedactTicket(string text, string ticket)
    {
        var redacted = new StringBuilder(text.Length);
        var remainder = text;
        while (remainder.IndexOf(ticket, StringComparison.Ordinal) is >= 0 and var index)
        {
            _ = redacted.Append(remainder, 0, index).Append(RedactedMarker);
            remainder = remainder[(index + ticket.Length)..];
        }

        _ = redacted.Append(remainder);
        return redacted.ToString();
    }

    private string WithStderrLocked(string message)
    {
        var tail = RenderStderrLocked();
        return tail.Length == 0 ? message : message + "\n" + tail;
    }

    /// <summary>
    /// Keeps the final 8 KiB of the pipe the way upstream's <c>onStderr</c> does: the chunk's own
    /// last 8 KiB, behind as much of the previous tail as still fits. A released contender still
    /// drains, into nothing.
    /// </summary>
    private void AppendTailLocked(byte[] buffer, int count)
    {
        if (_released)
        {
            return;
        }

        var kept = Math.Min(count, StderrLimit);
        var carried = Math.Min(_tail.Length, StderrLimit - kept);
        var next = new byte[carried + kept];
        Buffer.BlockCopy(_tail, _tail.Length - carried, next, 0, carried);
        Buffer.BlockCopy(buffer, count - kept, next, carried, kept);
        _tail = next;
    }

    /// <summary>Whether a poll has learned how the process ended, or learned that another reaper took it.</summary>
    private bool ExitObservedLocked() => _exit is not null;

    /// <summary>
    /// The one non-blocking exit look every observation shares. Windows reads the process handle
    /// while it is open; Unix reaps through <c>waitpid</c> with <c>WNOHANG</c>, which needs no handle
    /// and so keeps working after disposal. An <c>ECHILD</c> means someone else already reaped the
    /// pid (a host started with SIGCHLD ignored reaps every child), so there is nothing left to poll.
    /// </summary>
    private void PollExitLocked()
    {
        if (IsWindows)
        {
            if (_exit is null &&
                _process is { IsInvalid: false, IsClosed: false } handle &&
                GetExitCodeProcess(handle, out var code) &&
                code != StillActive)
            {
                _exit = new ChildExitStatus { ExitCode = (int)code };
            }

            return;
        }

        if (_exit is not null)
        {
            return;
        }

        var waited = WaitPid(ProcessId, out var status, WaitNoHang);
        if (waited == ProcessId)
        {
            _exit = ChildExitStatus.FromWaitStatus(status);
        }
        else if (waited < 0 && Marshal.GetLastWin32Error() == NoChild)
        {
            _exit = ChildExitStatus.Unknown;
        }
    }

    /// <summary>The contender's whole observation: the exit watch and the stderr drain, side by side.</summary>
    private async Task ObserveAsync()
    {
        var watch = WatchExitAsync();
        await DrainStderrAsync().ConfigureAwait(false);
        await watch.ConfigureAwait(false);
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            // The pipe stream the spawner handed over: on Unix its reads wait on the runtime's
            // event loop, on Windows on the completion port of the overlapped read end, so a
            // pending read holds no thread on either. The drain owns the stream from here and
            // closes it at end-of-stream.
#if NET
            await using (_stderr.ConfigureAwait(false))
            {
                await DrainPipeAsync(_stderr).ConfigureAwait(false);
            }
#else
            using (_stderr)
            {
                await DrainPipeAsync(_stderr).ConfigureAwait(false);
            }
#endif
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                // Disposal closes the pipe under the drain; that race is the drain ending, not a
                // contender failing.
                if (!_disposed)
                {
                    _error ??= exception;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _endOfStderr = true;
            }
        }
    }

    /// <summary>Consumes the pipe to end-of-stream, retaining the final 8 KiB unless released.</summary>
    private async Task DrainPipeAsync(Stream stderr)
    {
        var buffer = new byte[4096];
        while (true)
        {
#if NET
            var read = await stderr.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
#else
            var read = await stderr.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
#endif
            if (read == 0)
            {
                break;
            }

            lock (_gate)
            {
                AppendTailLocked(buffer, read);
            }
        }
    }

    /// <summary>
    /// Looks for the exit until it is observed: 1 ms first, doubling to a one-second cadence, on
    /// timers rather than a parked thread. Node learns the same fact from libuv's SIGCHLD handling,
    /// whatever the pipe is doing; .NET does not extend its own to children it did not start
    /// through <c>Process</c>. The election's reads poll too, so a contender it watches is seen at
    /// the loop's own cadence. Windows stops with disposal, which closes the handle it reads; Unix
    /// keeps going, since only this host can reap the pid.
    /// </summary>
    private async Task WatchExitAsync()
    {
        var delay = ExitWatchStart;
        while (true)
        {
            lock (_gate)
            {
                PollExitLocked();
                if (ExitObservedLocked() || (IsWindows && _disposed))
                {
                    return;
                }
            }

            await Task.Delay(delay).ConfigureAwait(false);
            delay = delay + delay < ExitWatchCap ? delay + delay : ExitWatchCap;
        }
    }
}
