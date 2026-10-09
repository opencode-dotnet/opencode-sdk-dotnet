using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The service fixture's <c>launcher-host</c> mode: a separate .NET process that owns one
/// standalone server, for the proofs whose subject is the server's owner — the owner killed
/// outright, a Ctrl+C sent to the owner's process group, the owner's own signal dispositions, the
/// job the owner itself runs in. On Linux and macOS the host is spawned in a session of its own, so
/// a signal sent to its group never reaches the test process. On Windows it is spawned through the
/// SDK's own spawn, so it inherits nothing of the test process, and, when the test asks, created
/// suspended and placed in a job of the test's before it runs. Its stdin is the test's lease on it:
/// closing it lets the host dispose its server and exit. After the server's pid the host reports one
/// stdout line per platform fact (<see cref="ReadReportAsync"/>): on Linux and macOS whether its
/// children are reaped automatically, on Windows whether the server shares its console; then, once
/// it observes it, the server's exit. Disposal ends the host on every path and waits for it; the
/// caller ends the server by <see cref="ServerProcessId"/> when the host could not.
/// </summary>
internal sealed class LauncherHost : IAsyncDisposable
{
    private static readonly TimeSpan StartBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(15);

    private readonly int _processId;
    private readonly Stream _lease;
    private readonly StreamReader _output;
    private readonly PipeLineReader _error;
    private readonly ConcurrentQueue<string> _errorLines = new();
    private readonly PosixChildExitWatch? _posixWatch;
    private readonly IWindowsExitWatch? _windowsWatch;
    private readonly SafeProcessHandle? _process;

    private LauncherHost(int processId, Stream? input, Stream? output, Stream? error, PosixChildExitWatch? posixWatch, IWindowsExitWatch? windowsWatch, SafeProcessHandle? process)
    {
        _processId = processId;
        _lease = input ?? throw new InvalidOperationException("The host's stdin was routed to a pipe.");
        _output = new StreamReader(output ?? throw new InvalidOperationException("The host's stdout was routed to a pipe."), Encoding.UTF8);
        _error = PipeLineReader.Start(error ?? throw new InvalidOperationException("The host's stderr was routed to a pipe."), _errorLines.Enqueue);
        _posixWatch = posixWatch;
        _windowsWatch = windowsWatch;
        _process = process;
    }

    /// <summary>Gets the host's pid, which on Linux and macOS also names its session and group.</summary>
    public int ProcessId => _processId;

    /// <summary>Gets the pid of the server the host started, once <see cref="StartAsync"/> returned.</summary>
    public int ServerProcessId { get; private set; }

    /// <summary>Gets the host's stderr lines so far, which include the server's own when the host hands it its stderr.</summary>
    public IReadOnlyList<string> ErrorLines => [.. _errorLines];

    /// <summary>Spawns the host and waits for it to report its server's pid.</summary>
    /// <param name="serverCommand">The server command the host starts.</param>
    /// <param name="environment">Entries the host and, through it, the server receive on top of the test's environment.</param>
    /// <param name="ignored">The signal the host starts with ignored, as a host started under <c>trap '' INT</c> or with <c>SIGCHLD</c> ignored does; Linux and macOS only.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The running host.</returns>
    public static async Task<LauncherHost> StartAsync(
        IReadOnlyList<string> serverCommand,
        IReadOnlyDictionary<string, string> environment,
        LauncherHostIgnores ignored,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await StartWindowsAsync(serverCommand, environment, hostJob: null, cancellationToken);
        }

        return await StartPosixAsync(serverCommand, environment, ignored, cancellationToken);
    }

    /// <summary>Spawns the host on Windows, suspended and placed in a job of the test's before it runs.</summary>
    /// <param name="serverCommand">The server command the host starts.</param>
    /// <param name="environment">Entries the host and, through it, the server receive on top of the test's environment.</param>
    /// <param name="hostJob">The job the host runs in.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The running host.</returns>
    public static Task<LauncherHost> StartInJobAsync(
        IReadOnlyList<string> serverCommand,
        IReadOnlyDictionary<string, string> environment,
        SafeJobHandle hostJob,
        CancellationToken cancellationToken) =>
        StartWindowsAsync(serverCommand, environment, hostJob, cancellationToken);

    /// <summary>Reads the host's next stdout report line, waiting at most the start bound.</summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The line; null once the host's stdout ended.</returns>
    public async Task<string?> ReadReportAsync(CancellationToken cancellationToken)
    {
#if NET
        return await _output.ReadLineAsync(cancellationToken).AsTask().WaitAsync(StartBound, cancellationToken);
#else
        return await _output.ReadLineAsync().WaitAsync(StartBound, cancellationToken);
#endif
    }

    /// <summary>Closes the host's lease, so it disposes its server and exits.</summary>
    /// <returns>A task that completes once the lease is closed.</returns>
    public ValueTask ReleaseLeaseAsync() => _lease.DisposeAsync();

    /// <summary>Ends the host outright, as a crash would: no disposal of its server runs.</summary>
    /// <returns>True when the kill was delivered.</returns>
    public bool Kill() =>
        _process is { } process
            ? WindowsInterop.TerminateProcess(process, 1)
            : PosixProcessProbe.SignalGroup(_processId, PosixProcessProbe.Kill);

    /// <summary>Waits at most <paramref name="bound"/> for the host to exit.</summary>
    /// <param name="bound">How long the test waits.</param>
    /// <returns>True when the host exited inside the bound.</returns>
    public async Task<bool> ExitedWithinAsync(TimeSpan bound)
    {
        try
        {
            _ = _windowsWatch is { } windowsWatch
                ? await ChildExitObservation.WithinAsync(windowsWatch, bound)
                : await ChildExitObservation.WithinAsync(_posixWatch!, bound);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends the host by closing its lease, so it disposes its server, and waits for the host's
    /// stderr to reach end-of-stream: every writer of it is then gone, and
    /// <see cref="ErrorLines"/> holds every line anything wrote there.
    /// </summary>
    /// <returns>True when the host exited and its stderr reached end-of-stream inside the bounds.</returns>
    public async Task<bool> EndAsync()
    {
        await _lease.DisposeAsync();
        if (!await ExitedWithinAsync(ExitBound))
        {
            return false;
        }

        try
        {
#pragma warning disable VSTHRD003 // The reader's end-of-stream is what this end waits for; the reader runs on the pool, not on a joined context.
            return await _error.Completion.WaitAsync(ExitBound);
#pragma warning restore VSTHRD003
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Ends the host: closes its lease so it disposes its server, then kills it if it does not exit.</summary>
    /// <returns>A task that completes once the host has exited.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _lease.DisposeAsync();
            if (!await ExitedWithinAsync(ExitBound))
            {
                _ = Kill();
                _ = await ExitedWithinAsync(ExitBound);
            }
        }
        finally
        {
            _output.Dispose();
            await _error.DisposeAsync();

            // The watch owns the process handle and releases it once the host is gone; the
            // handle is closed here as well, for the host that outlived its kill.
            _windowsWatch?.Dispose();
            _process?.Dispose();
        }
    }

    /// <summary>Describes the host's stderr so far, for a failed assertion.</summary>
    /// <returns>The stderr lines.</returns>
    public string DescribeError() => string.Join(" | ", _errorLines);

    private static IReadOnlyList<string> HostArguments(IReadOnlyList<string> fixture, IReadOnlyList<string> serverCommand) =>
        [fixture[1], "launcher-host", .. serverCommand];

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> environment)
    {
        var merged = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in LauncherSeams.ForCurrentProcess().HostEnvironment.Concat(environment))
        {
            merged[entry.Key] = entry.Value;
        }

        return merged;
    }

    private static async Task<LauncherHost> StartPosixAsync(
        IReadOnlyList<string> serverCommand,
        IReadOnlyDictionary<string, string> environment,
        LauncherHostIgnores ignored,
        CancellationToken cancellationToken)
    {
        var fixture = new ServiceFixtureCommand(new RealFileSystem()).Resolve();
        var hostArguments = HostArguments(fixture, serverCommand);

        // A shell or perl that ignores the signal and then becomes the host keeps the ignore across
        // exec. Perl ignores SIGCHLD through sigaction in the process that becomes the host, so the
        // kernel treats the host's children as it treats those of any process that ignores it.
        string executable;
        IReadOnlyList<string> arguments;
        switch (ignored)
        {
            case LauncherHostIgnores.Interrupt:
                executable = "/bin/sh";
                arguments = ["-c", "trap '' INT; exec \"$0\" \"$@\"", fixture[0], .. hostArguments];
                break;
            case LauncherHostIgnores.ChildExit:
                executable = "perl";
                arguments = ["-e", "$SIG{CHLD} = 'IGNORE'; exec { $ARGV[0] } @ARGV or die \"exec: $!\";", fixture[0], .. hostArguments];
                break;
            default:
                executable = fixture[0];
                arguments = hostArguments;
                break;
        }

        var child = new PosixSpawn().Spawn(new PosixSpawnRequest
        {
            ExecutablePath = executable,
            Arguments = arguments,
            Environment = Merge(environment),
            StandardInput = ChildStreamRoute.Pipe,
            StandardOutput = ChildStreamRoute.Pipe,
            StandardError = ChildStreamRoute.Pipe,
        });
        var watch = PosixChildExitWatch.Start(child.ProcessId, new ChildExitStatusReader(), new ProcessGroupSignal(), childrenReapedAutomatically: false);
        return await ReadyAsync(
            new LauncherHost(child.ProcessId, child.StandardInput, child.StandardOutput, child.StandardError, watch, windowsWatch: null, process: null),
            cancellationToken);
    }

    private static async Task<LauncherHost> StartWindowsAsync(
        IReadOnlyList<string> serverCommand,
        IReadOnlyDictionary<string, string> environment,
        SafeJobHandle? hostJob,
        CancellationToken cancellationToken)
    {
        var fixture = new ServiceFixtureCommand(new RealFileSystem()).Resolve();
        var command = WindowsCommandLine.For(
            new ResolvedExecutable(fixture[0], fixture[0], IsBatchScript: false), HostArguments(fixture, serverCommand), []);
        var child = new WindowsSpawn().Spawn(new WindowsSpawnRequest
        {
            ApplicationPath = command.ApplicationPath,
            CommandLine = command.CommandLine,
            Environment = Merge(environment),
            StandardStreams = new WindowsStandardStreams
            {
                Input = ChildStreamRoute.Pipe,
                Output = ChildStreamRoute.Pipe,
                Error = ChildStreamRoute.Pipe,
            },
            StartSuspended = hostJob is not null,
        });
        IWindowsExitWatch watch;
        try
        {
            if (hostJob is not null && child.MainThread is { } thread)
            {
                if (!WindowsInterop.AssignProcessToJobObject(hostJob, child.Process) || WindowsInterop.ResumeThread(thread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                thread.Dispose();
            }

            watch = new WindowsChildExit().Watch(child.Process);
        }
        catch
        {
            _ = WindowsInterop.TerminateProcess(child.Process, 1);
            child.MainThread?.Dispose();
            child.Process.Dispose();
            foreach (var stream in new[] { child.StandardInput, child.StandardOutput, child.StandardError })
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync();
                }
            }

            throw;
        }

        return await ReadyAsync(
            new LauncherHost(child.ProcessId, child.StandardInput, child.StandardOutput, child.StandardError, posixWatch: null, watch, child.Process),
            cancellationToken);
    }

    private static async Task<LauncherHost> ReadyAsync(LauncherHost host, CancellationToken cancellationToken)
    {
        try
        {
            host.ServerProcessId = await host.ReadServerPidAsync(cancellationToken);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private async Task<int> ReadServerPidAsync(CancellationToken cancellationToken)
    {
        const string prefix = "server pid=";
        var line = await ReadReportAsync(cancellationToken);
        if (line is not null &&
            line.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return pid;
        }

        _ = await ExitedWithinAsync(ExitBound);
        throw new InvalidOperationException(
            $"The launcher host reported no server pid (first line '{line}'): {DescribeError()}");
    }
}
