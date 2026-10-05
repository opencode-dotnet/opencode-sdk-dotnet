using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// One mode of the isolated service fixture, started and owned by a test: a real process of this
/// machine with a known pid, alive until the test's subject ends it or disposal kills it. A
/// lingering mode (<c>idle</c>, <c>ignore-sigterm</c>) prints <c>ready</c>; a daemon stand-in
/// (<c>contender-probe stall</c> or <c>stale</c>) prints <c>ready pid=&lt;pid&gt; port=&lt;port&gt;</c>
/// naming the loopback port it bound, so a test can seed a registration for it. Readiness is that
/// first line, so a snapshot taken after a start returns names a process that is fully started.
/// Its stderr is collected the way the launcher collects a child's, through the asynchronous read
/// the process class drives, so nothing here holds a task over the process's streams.
/// </summary>
internal sealed class ServiceFixtureProcess : IAsyncDisposable
{
    private const string ReadyLine = "ready";
    private const string PortMarker = " port=";
    private static readonly TimeSpan DisposalBound = TimeSpan.FromSeconds(15);

    private readonly Process _process;
    private readonly List<string> _standardError = [];
    private readonly Lock _standardErrorLock = new();
    private int? _port;
    private int _disposed;

    private ServiceFixtureProcess(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, received) =>
        {
            if (received.Data is null)
            {
                return;
            }

            lock (_standardErrorLock)
            {
                _standardError.Add(received.Data);
            }
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>Gets the pid the operating system gave the fixture process.</summary>
    public int ProcessId => _process.Id;

    /// <summary>Gets a value indicating whether the process has exited.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Gets the endpoint a daemon stand-in bound: <c>http://127.0.0.1:&lt;port&gt;</c>.</summary>
    public Uri Endpoint => new("http://127.0.0.1:" + (_port ?? throw new InvalidOperationException(
        "Only a daemon stand-in reports a port.")).ToString(CultureInfo.InvariantCulture));

    /// <summary>Starts one lingering mode and waits for its <c>ready</c> line.</summary>
    /// <param name="fileSystem">The filesystem the fixture build is resolved through.</param>
    /// <param name="mode">The fixture mode: <c>idle</c> or <c>ignore-sigterm</c>.</param>
    /// <param name="cancellationToken">The caller's bound; the child is killed when it is cancelled before readiness.</param>
    /// <returns>The owned process.</returns>
    public static Task<ServiceFixtureProcess> StartAsync(IFileSystem fileSystem, string mode, CancellationToken cancellationToken) =>
        StartAsync(fileSystem, [mode], expectsPort: false, cancellationToken);

    /// <summary>Starts one daemon stand-in and waits for its ready line naming pid and port.</summary>
    /// <param name="fileSystem">The filesystem the fixture build is resolved through.</param>
    /// <param name="mode">The stand-in mode: <c>stall</c> or <c>stale</c>.</param>
    /// <param name="cancellationToken">The caller's bound; the child is killed when it is cancelled before readiness.</param>
    /// <returns>The owned stand-in.</returns>
    public static Task<ServiceFixtureProcess> StartDaemonStandInAsync(IFileSystem fileSystem, string mode, CancellationToken cancellationToken) =>
        StartAsync(fileSystem, ["contender-probe", mode], expectsPort: true, cancellationToken);

    /// <summary>Waits at most <paramref name="bound"/> for the process to exit.</summary>
    /// <param name="bound">The most this call waits.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>True when the process exited inside the bound.</returns>
    public Task<bool> ObserveExitWithinAsync(TimeSpan bound, CancellationToken cancellationToken) =>
        ProcessObservation.ObserveExitWithinAsync(_process, bound, cancellationToken);

    /// <summary>
    /// Ends the process when it is still running and releases it; idempotent, so a test may end
    /// the process early and still leave the <c>await using</c> in place. A process that leaks
    /// past the test inherits the test host's console handles and keeps <c>dotnet test</c> alive
    /// after the host exits, which is why every start sits inside an <c>await using</c>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 1)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _ = TestProcessTreeKill.TryKill(_process);
                _ = await ObserveExitWithinAsync(DisposalBound, CancellationToken.None);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static async Task<ServiceFixtureProcess> StartAsync(IFileSystem fileSystem, string[] modeArguments, bool expectsPort,
        CancellationToken cancellationToken)
    {
        var command = new ServiceFixtureCommand(fileSystem).Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            Arguments = ProcessArgumentComposer.Compose([.. command.Skip(1), .. modeArguments]),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Starting '{startInfo.FileName}' returned no process.");
        var fixture = new ServiceFixtureProcess(process);
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            bool ready;
            if (expectsPort)
            {
                ready = TryParsePort(line, out var port);
                fixture._port = port;
            }
            else
            {
                ready = string.Equals(line, ReadyLine, StringComparison.Ordinal);
            }

            if (!ready)
            {
                throw new InvalidOperationException(
                    $"The fixture mode '{string.Join(' ', modeArguments)}' printed '{line}' instead of its ready line. {fixture.StandardErrorTail()}");
            }
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }

        return fixture;
    }

    /// <summary>Reads the <c>ready pid=&lt;pid&gt; port=&lt;port&gt;</c> line's port.</summary>
    private static bool TryParsePort(string? line, out int port)
    {
        port = 0;
        var index = line?.IndexOf(PortMarker, StringComparison.Ordinal) ?? -1;
        if (line is null || index < 0)
        {
            return false;
        }

        var digits = line[(index + PortMarker.Length)..];
        var end = 0;
        while (end < digits.Length && digits[end] is >= '0' and <= '9')
        {
            end++;
        }

        return end > 0 && int.TryParse(digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    private string StandardErrorTail()
    {
        lock (_standardErrorLock)
        {
            return string.Join(Environment.NewLine, _standardError);
        }
    }
}
