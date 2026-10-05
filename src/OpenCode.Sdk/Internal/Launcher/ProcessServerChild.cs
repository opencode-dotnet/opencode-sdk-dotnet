using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using OpenCode.Sdk.Internal.Diagnostics;
using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// The Windows strategy, on <see cref="Process"/>: the child's own pipes read on dedicated
/// threads, stdin as the ownership lease, and a ladder that closes the lease, waits the grace, and
/// then ends the whole tree.
/// </summary>
internal sealed class ProcessServerChild : ServerChild
{
    /// <summary>How long the forced rung waits for the root after the tree kill.</summary>
    private static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounds every wait for the child's redirected output to reach end-of-stream. End-of-stream
    /// arrives only when every process holding a write end closes it, and a surviving descendant
    /// can hold one open, so a caller always regains control within this window regardless of
    /// what the child (or anything the child spawned) is still holding open.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly DedicatedThreadOutputPump _pump;
    private readonly ProcessTreeTerminator _terminator;
    private readonly OpenCodeServerOutput? _output;
    private readonly TimeSpan _grace;
    private readonly TaskCompletionSource<ChildExitStatus> _exited;

    private ProcessServerChild(
        Process process,
        DedicatedThreadOutputPump pump,
        ProcessTreeTerminator terminator,
        ServerChildStart start,
        TaskCompletionSource<ChildExitStatus> exited)
    {
        _process = process;
        _pump = pump;
        _terminator = terminator;
        _output = start.Output;
        _grace = start.GracefulShutdownTimeout;
        _exited = exited;

        // Captured while the handle is live: the identity stays readable after the handle is released.
        ProcessId = process.Id;
    }

    /// <inheritdoc />
    public override int ProcessId { get; }

    /// <inheritdoc />
    public override Task<ChildExitStatus> Exited => _exited.Task;

    /// <inheritdoc />
    public override Task ReadersEnded => _pump.ReadersEnded;

    /// <summary>Creates and starts the process and its reader threads.</summary>
    /// <param name="start">What to start.</param>
    /// <param name="terminator">The tree kill for this child's failed start and disposal.</param>
    /// <returns>The started child.</returns>
    public static ProcessServerChild Launch(ServerChildStart start, ProcessTreeTerminator terminator)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(terminator);

        var process = CreateProcess(start);
        try
        {
            var exited = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult(ReadExit(process));
            try
            {
                _ = process.Start();
            }
            catch (Win32Exception exception)
            {
                throw new OpenCodeServerException(
                    $"Failed to start the server command '{start.Executable.Command}'{DescribeResolution(start.Executable)}.",
                    exception);
            }

            var pump = DedicatedThreadOutputPump.Begin(process, start.OnStandardOutput, start.OnStandardError);
            return new ProcessServerChild(process, pump, terminator, start, exited);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task<ReadinessOutcome> WaitForReadinessAsync(Task<string> readyLine, CancellationToken readiness)
    {
        ArgumentNullException.ThrowIfNull(readyLine);

        var exit = _process.WaitForExitAsync(CancellationToken.None);
        _ = await Task.WhenAny(readyLine, exit).WaitAsync(readiness).ConfigureAwait(false);
        if (readyLine.IsCompleted)
        {
            return ReadinessOutcome.Ready(await readyLine.ConfigureAwait(false));
        }

        var exitCode = _process.ExitCode;

        // The root already exited on its own, but a launcher shim (a .cmd/bun wrapper that
        // spawns the real server and exits) can leave live grandchildren holding the redirected
        // pipe handles open — which would make the drain below wait for an EOF that never comes.
        // Killing the tree first closes that gap before the bounded drain runs; whether there was
        // still a tree to end, and whether the drain reached EOF inside its bound, change nothing
        // about what this failure reports.
        _ = _terminator.TryKill(_process);
        _ = await _pump.DrainAsync(DrainTimeout).ConfigureAwait(false);
        return ReadinessOutcome.Exited(new ChildExitStatus { ExitCode = exitCode });
    }

    /// <inheritdoc />
    public override async Task EndFailedStartAsync() => _ = await EndStartupFailureAsync().ConfigureAwait(false);

    /// <summary>
    /// Ends a child that failed to reach readiness and drains its redirected output, so the stderr
    /// tail the caller is about to quote is as complete as the bound allows.
    /// </summary>
    /// <returns>
    /// True when the drain ran to completion; false when it could not run at all. Either way the
    /// startup failure reaches the caller as its own exception rather than as a teardown fault,
    /// which is why the caller discards this: an incomplete tail is still the best evidence
    /// available, and there is no second attempt worth making on a process being abandoned.
    /// </returns>
    private async Task<bool> EndStartupFailureAsync()
    {
        _ = _terminator.TryKill(_process);
        try
        {
            if (!await ProcessRootExit.WaitWithinAsync(_process, ForcedExitTimeout).ConfigureAwait(false))
            {
                // The bounded exit wait expired without the process ending; skip the drain rather
                // than wait on a process that may still be alive and writing.
                return false;
            }

            return await _pump.DrainAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No process is associated with the object any more: there is nothing left to wait
            // for, and nothing left holding the redirected pipes open either.
            return false;
        }
        catch (Win32Exception)
        {
            // The handle is gone or inaccessible; the release that follows frees what remains.
            return false;
        }
    }

    /// <summary>
    /// Closes stdin (the ownership lease), waits the configured grace, then escalates to a forced
    /// tree kill; then lets the redirected readers reach end-of-stream inside the drain bound, so a
    /// collector's final snapshot holds what the child wrote while it was ended.
    /// </summary>
    /// <inheritdoc />
    public override async Task EndAsync()
    {
        try
        {
            await EndOwnedChildAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_output is not null)
            {
                // The collector's promise is a final snapshot once disposal returns. The child was
                // ended above, and a drain that cannot finish inside its bound leaves an honest,
                // possibly incomplete, tail.
                _ = await _pump.DrainAsync(DrainTimeout).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            // Every reader ends before the process is released, collector or not: a read still
            // waiting for end-of-stream (a descendant holding the pipe) is canceled here.
            await _pump.ReleaseAsync().ConfigureAwait(false);
            _output?.Complete();
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static ChildExitStatus ReadExit(Process process)
    {
        try
        {
            return new ChildExitStatus { ExitCode = process.ExitCode };
        }
        catch (InvalidOperationException)
        {
            // The handle was released while the notification ran; the code is no longer readable.
            return ChildExitStatus.Unknown;
        }
    }

    private static Process CreateProcess(ServerChildStart start)
    {
        var process = new Process();
        var startInfo = process.StartInfo;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        if (start.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = start.WorkingDirectory;
        }

        ConfigureCommandLine(startInfo, start);
        if (start.Environment is not null)
        {
            foreach (var entry in start.Environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        // The explicit entry wins over anything inherited or supplied: the child's lease
        // credential is always this start's own (upstream standalone.ts command's posture; stdio
        // mode scrubs it from the env the server hands to tools, server-process.ts).
        startInfo.Environment[ChildEnvironmentComposer.PasswordVariable] = start.Password;
        process.EnableRaisingEvents = true;
        return process;
    }

    /// <summary>
    /// Points the start info at what actually runs and composes its command line. A batch shim
    /// never becomes the FileName: cmd.exe does, with the script as its first quoted token, so the
    /// launch is one documented parse instead of CreateProcess's implicit batch handling. The
    /// consequences are real and deliberate — the redirected stdin lease and the stdout readiness
    /// line pass through the interpreter to the child, and the owned root this launcher reports as
    /// its pid is that interpreter, whose descendants the bounded tree kill covers.
    /// </summary>
    private static void ConfigureCommandLine(ProcessStartInfo startInfo, ServerChildStart start)
    {
        var executable = start.Executable;
        if (executable.IsBatchScript)
        {
            startInfo.FileName = BatchCommandLine.InterpreterPath;

            // One composed string on every target: cmd.exe does not follow the MSVCRT rules
            // ArgumentList applies, so the batch case never routes through that door.
            startInfo.Arguments = BatchCommandLine.Compose(
                executable.Path, start.SuppliedArguments, start.LauncherArguments);
            return;
        }

        startInfo.FileName = executable.Path;
        var arguments = start.SuppliedArguments.Concat(start.LauncherArguments);
#if NET
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
#else
        // ArgumentList does not exist downlevel; the composed string follows the MSVCRT rules.
        startInfo.Arguments = ProcessArgumentComposer.Compose(arguments);
#endif
    }

    private async Task EndOwnedChildAsync()
    {
        if (ProcessRootExit.HasExited(_process))
        {
            return;
        }

        // Stdin EOF ends the scoped server lifetime (server-process.ts); closing the
        // redirected writer is the lease release. A lease that was already gone means the child
        // is already leaving, so the bounded wait below covers both outcomes.
        ReleaseStdinLease();

        // The child's own exit, not its output's end-of-stream: a descendant still holding the
        // pipe must not hold the grace open. The drain has its own bound.
        if (await ProcessRootExit.WaitWithinAsync(_process, _grace).ConfigureAwait(false))
        {
            return;
        }

        // An incomplete kill (some process of the tree refused it) still ended what it could
        // reach; the ladder continues the same way, and every release step after it runs.
        _ = _terminator.TryKill(_process);

        // Bounded on purpose: the kill was issued, and a disposal never hangs the caller, so a
        // child the operating system has not reaped inside this window is left to it.
        _ = await ProcessRootExit.WaitWithinAsync(_process, ForcedExitTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the ownership lease by closing the redirected stdin writer; a pipe that is already
    /// gone reports the child leaving rather than a failure to end it.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "Closing the lease is the release itself: a writer that went with the process, or a broken pipe, reports that the child is already leaving, which is what the release asks for.")]
    private void ReleaseStdinLease()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // The lease is released either way: the child is already leaving.
        }
    }
}
