using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The service fixture's <c>launcher-host</c> mode on Linux and macOS: a separate .NET process that
/// owns one standalone server, for the proofs whose subject is the server's owner — a Ctrl+C sent
/// to the owner's process group, the owner killed outright, the owner's own signal dispositions.
/// The host is spawned in a session of its own, so a signal sent to its group never reaches the
/// test process. Its stdin is the test's lease on it: closing it lets the host dispose its server
/// and exit. After the server's pid the host reports, one stdout line each, whether its children
/// are reaped automatically and, once it observes it, the server's exit
/// (<see cref="ReadReportAsync"/>). Disposal ends the host on every path and waits for it; the
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
    private readonly PosixChildExitWatch _watch;

    private LauncherHost(PosixSpawnedChild child)
    {
        _processId = child.ProcessId;
        _lease = child.StandardInput ?? throw new InvalidOperationException("The host's stdin was routed to a pipe.");
        _output = new StreamReader(
            child.StandardOutput ?? throw new InvalidOperationException("The host's stdout was routed to a pipe."), Encoding.UTF8);
        _error = PipeLineReader.Start(
            child.StandardError ?? throw new InvalidOperationException("The host's stderr was routed to a pipe."), _errorLines.Enqueue);
        _watch = PosixChildExitWatch.Start(child.ProcessId, new ChildExitStatusReader(), new ProcessGroupSignal(), childrenReapedAutomatically: false);
    }

    /// <summary>Gets the host's pid, which also names its session and group.</summary>
    public int ProcessId => _processId;

    /// <summary>Gets the pid of the server the host started, once <see cref="StartAsync"/> returned.</summary>
    public int ServerProcessId { get; private set; }

    /// <summary>Gets the host's stderr lines so far, which include the server's own when the host hands it its stderr.</summary>
    public IReadOnlyList<string> ErrorLines => [.. _errorLines];

    /// <summary>Spawns the host and waits for it to report its server's pid.</summary>
    /// <param name="serverCommand">The server command the host starts.</param>
    /// <param name="environment">Entries the host and, through it, the server receive on top of the test's environment.</param>
    /// <param name="ignored">The signal the host starts with ignored, as a host started under <c>trap '' INT</c> or with <c>SIGCHLD</c> ignored does.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The running host.</returns>
    public static async Task<LauncherHost> StartAsync(
        IReadOnlyList<string> serverCommand,
        IReadOnlyDictionary<string, string> environment,
        LauncherHostIgnores ignored,
        CancellationToken cancellationToken)
    {
        var fixture = new ServiceFixtureCommand(new RealFileSystem()).Resolve();
        var hostArguments = new List<string> { fixture[1], "launcher-host" };
        hostArguments.AddRange(serverCommand);
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in LauncherSeams.ForCurrentProcess().HostEnvironment.Concat(environment))
        {
            merged[entry.Key] = entry.Value;
        }

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

        var request = new PosixSpawnRequest
        {
            ExecutablePath = executable,
            Arguments = arguments,
            Environment = merged,
            StandardInput = ChildStreamRoute.Pipe,
            StandardOutput = ChildStreamRoute.Pipe,
            StandardError = ChildStreamRoute.Pipe,
        };
        var host = new LauncherHost(new PosixSpawn().Spawn(request));
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

    /// <summary>Waits at most <paramref name="bound"/> for the host to exit.</summary>
    /// <param name="bound">How long the test waits.</param>
    /// <returns>True when the host exited inside the bound.</returns>
    public async Task<bool> ExitedWithinAsync(TimeSpan bound)
    {
        try
        {
            _ = await ChildExitObservation.WithinAsync(_watch, bound);
            return true;
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
                _ = PosixProcessProbe.SignalGroup(_processId, PosixProcessProbe.Kill);
                _ = await ExitedWithinAsync(ExitBound);
            }
        }
        finally
        {
            _output.Dispose();
            await _error.DisposeAsync();
        }
    }

    /// <summary>Describes the host's stderr so far, for a failed assertion.</summary>
    /// <returns>The stderr lines.</returns>
    public string DescribeError() => string.Join(" | ", _errorLines);

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
