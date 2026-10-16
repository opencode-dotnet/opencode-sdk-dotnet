using System.Globalization;
using System.Text;
using OpenCode.Sdk.Internal;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The hermetic arrangement one Ensure live test builds on, per test rather than per session: the
/// isolated XDG roots, a reserved free loopback port (never the maintainer's real daemon's), an
/// empty config seed naming that port, the channel's registration path (<c>local</c> for the
/// source run, the shared <c>service.json</c> a release build uses otherwise), and the
/// forwarding <c>opencode</c> shim the Ensure loop's default command resolves. The spawned service
/// is the launcher's to make ready and to end; this type only owns the boundary. Every election it
/// runs records its contenders in the <see cref="ContenderLedger"/>. On disposal it lets the
/// election settle the way the pinned CLI's own election test does before it ends the winner —
/// every loser leaves once it finds the elected service — then ends every recorded contender and
/// whatever is registered, waits for each to leave, refuses a registration a process took over
/// behind its back, and removes the run root — or keeps it, with the daemon log's tail printed,
/// when the test failed.
/// </summary>
internal sealed class EnsureServiceContext : IAsyncDisposable
{
    private const int RealDaemonPort = 49374;
    private const int LogTailLines = 40;
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The bound the pinned CLI's election test gives its losers to leave
    /// (<c>concurrent service processes elect one server</c> in <c>packages/cli/test/service.test.ts</c>).
    /// </summary>
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(60);

    private readonly RealFileSystem _fileSystem = new();
    private TestRunRoot? _runRoot;
    private IsolationBoundary? _isolation;
    private string? _registrationFile;
    private string? _shimDirectory;
    private string? _shimPath;
    private string? _ledgerPath;
    private ContenderLedger? _ledger;
    private string? _channelFile;
    private int _port;
    private int _disposed;
    private bool _keep;

    private EnsureServiceContext()
    {
    }

    /// <summary>Builds and initializes one context on the source run's <c>local</c> channel.</summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The initialized context.</returns>
    public static Task<EnsureServiceContext> CreateAsync(CancellationToken cancellationToken) =>
        CreateAsync("service-local.json", cancellationToken);

    /// <summary>
    /// Builds and initializes one context on a release build's channel: <c>latest</c>, <c>beta</c>,
    /// <c>dev</c>, and <c>next</c> all register and read their config as <c>service.json</c>
    /// (<c>filename</c> in the CLI's <c>service-config.ts</c>).
    /// </summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The initialized context.</returns>
    public static Task<EnsureServiceContext> CreateForReleaseBuildAsync(CancellationToken cancellationToken) =>
        CreateAsync("service.json", cancellationToken);

    /// <summary>Gets the per-run root every global directory is redirected into.</summary>
    public string RunRoot => _runRoot!.Path;

    /// <summary>Gets the isolated roots the spawned service and any isolated fixture process read.</summary>
    public IReadOnlyDictionary<string, string> Environment => Isolation.Environment;

    /// <summary>Gets the isolation boundary every service this context elects must be seen to honour.</summary>
    public IsolationBoundary Isolation => _isolation ?? throw new InvalidOperationException("The context is initialized before its isolation is read.");

    /// <summary>Gets the channel's registration path under the isolated state root.</summary>
    public string RegistrationFile => _registrationFile!;

    /// <summary>Gets the reserved loopback port the spawned service binds.</summary>
    public int Port => _port;

    /// <summary>Gets the directory holding the forwarding <c>opencode</c> shim.</summary>
    public string ShimDirectory => _shimDirectory!;

    /// <summary>Gets the shim's full path, for an explicit <see cref="OpenCodeServerEnsureOptions.Command"/>.</summary>
    public string ShimPath => _shimPath!;

    /// <summary>Gets the ledger every election this context runs records its contenders in; the isolated fixture process records into it too.</summary>
    public string LedgerPath => _ledgerPath!;

    /// <summary>Runs one Ensure election through the ledger at the pinned timing.</summary>
    /// <param name="options">The ensure options.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The non-owning handle Ensure returns.</returns>
    public Task<OpenCodeServer> EnsureAsync(OpenCodeServerEnsureOptions options, CancellationToken cancellationToken) =>
        EnsureAsync(options, ServiceTiming.Default, cancellationToken);

    /// <summary>Runs one Ensure election through the ledger at an injected timing.</summary>
    /// <param name="options">The ensure options.</param>
    /// <param name="timing">The lifecycle timing.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The non-owning handle Ensure returns.</returns>
    public async Task<OpenCodeServer> EnsureAsync(OpenCodeServerEnsureOptions options, ServiceTiming timing, CancellationToken cancellationToken)
    {
        var server = await OpenCodeServer.EnsureWithSeamsAsync(options, timing, Ledger, cancellationToken).ConfigureAwait(false);

        // The handle does not own the elected service; a service that fails the check is ended by
        // this context's teardown like every other one it recorded.
        Isolation.ConfirmHonored("The service the election chose");
        return server;
    }

    /// <summary>Reads every recorded contender still running, identified by this process.</summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The live contenders.</returns>
    public Task<IReadOnlyList<ProcessMark>> ReadLiveContendersAsync(CancellationToken cancellationToken) =>
        Ledger.ReadLiveAsync(cancellationToken);

    /// <summary>
    /// Waits until the elections this context ran have settled: every recorded contender has left
    /// except, while the registration names a live service, the one contender hosting it — the
    /// losers find the elected service and exit on their own, which the pinned CLI's election test
    /// waits for before it ends the winner. On Unix the shim execs the server, so a contender is the
    /// server itself; on Windows it is the batch shim's cmd.exe host, which lives exactly as long as
    /// the server it waits on.
    /// </summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>True when the elections settled inside the pinned bound.</returns>
    public async Task<bool> SettleAsync(CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(SettleBound);

        var contenders = await Ledger.ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        var hosting = await ReadRegisteredAsync().ConfigureAwait(false) is null ? 0 : 1;
        var held = HeldProcess.HoldAll(_fileSystem, contenders);
        var exits = held.Select(process => process.WaitForTerminationAsync(SettleBound, bound.Token)).ToList();
        try
        {
            while (exits.Count > hosting)
            {
                var exited = await Task.WhenAny(exits).ConfigureAwait(false);
                if (!await exited.ConfigureAwait(false))
                {
                    return false;
                }

                _ = exits.Remove(exited);
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            // The wait on the winner's host is the one still pending; it has nothing left to tell.
            await bound.CancelOnWorkerAsync().ConfigureAwait(false);
            HeldProcess.ReleaseAll(held);
        }
    }

    private ContenderLedger Ledger => _ledger ?? throw new InvalidOperationException("The context is initialized before any election runs.");

    /// <summary>Keeps the run root, and prints the daemon log's tail, instead of removing it: the evidence a failed test needs.</summary>
    public void KeepForDiagnosis() => _keep = true;

    /// <summary>
    /// The environment an isolated fixture process needs: the isolated roots plus a PATH that puts
    /// the shim first, so the default <c>opencode serve --service</c> command resolves
    /// <c>opencode</c> to the forwarding shim rather than any installed CLI.
    /// </summary>
    public Dictionary<string, string?> IsolatedProcessEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        var existing = System.Environment.GetEnvironmentVariable("PATH");
        environment["PATH"] = ShimDirectory + _fileSystem.Path.PathSeparator + existing;
        return environment;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 1)
        {
            return;
        }

        // Ending the winner while a loser is still starting hands the registration to that loser:
        // it finds nothing registered and elects itself. Past the bound, whatever is left is ended
        // regardless, and the takeover check below reports a loser that got there first.
        _ = await SettleAsync(CancellationToken.None).ConfigureAwait(false);

        var targets = new HashSet<ProcessMark>(await Ledger.ReadLiveAsync(CancellationToken.None).ConfigureAwait(false));
        if (await ReadRegisteredAsync().ConfigureAwait(false) is { } registered)
        {
            _ = targets.Add(registered);
        }

        await EndAsync(targets).ConfigureAwait(false);

        // Everything recorded has been ended, so a registration that still names a live process is
        // a failure of this boundary: end that process too, keep the evidence, and fail the test.
        if (await ReadRegisteredAsync().ConfigureAwait(false) is { } survivor)
        {
            await EndAsync([survivor]).ConfigureAwait(false);
            _keep = true;
            var runRoot = RunRoot;
            await ReleaseRunRootAsync().ConfigureAwait(false);
            var pid = survivor.ProcessId.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException(targets.Contains(survivor)
                ? $"Registered process {pid} did not leave within {ExitBound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s of being ended; run root kept: {runRoot}"
                : $"Process {pid} took the registration over after every recorded contender was ended, so no election of this context recorded it; run root kept: {runRoot}");
        }

        await ReleaseRunRootAsync().ConfigureAwait(false);
    }

    /// <summary>Ends the tree of each marked process that still runs, then waits for each of those to be finished.</summary>
    private async Task EndAsync(IEnumerable<ProcessMark> targets) =>
        // A run root is removed only once nothing started under it still holds a file in it.
        _ = await HeldProcess.EndAllAsync(_fileSystem, targets, ExitBound).ConfigureAwait(false);

    /// <summary>Removes the run root, or keeps it with the daemon log's tail printed when a failure asked for the evidence.</summary>
    private async Task ReleaseRunRootAsync()
    {
        if (_runRoot is null)
        {
            return;
        }

        var keep = _keep || string.Equals(
            System.Environment.GetEnvironmentVariable("OPENCODE_SDK_TESTS_KEEP_LOGS"),
            "1",
            StringComparison.Ordinal);
        if (keep)
        {
            Console.WriteLine("Ensure service context retained; run root: " + RunRoot);
            Console.WriteLine(await DaemonLogTailAsync().ConfigureAwait(false));
        }
        else
        {
            _runRoot.Dispose();
        }

        _runRoot = null;
    }

    /// <summary>The mark of the live process the registration names, or null when none is registered or it is gone.</summary>
    private async Task<ProcessMark?> ReadRegisteredAsync() =>
        _registrationFile is { } file
        && await ServiceRegistrationReader.TryReadAsync(new TestablyServiceFileSystem(_fileSystem), file, CancellationToken.None).ConfigureAwait(false) is { } registration
            ? ProcessMark.TryRead(_fileSystem, registration.ProcessId)
            : null;

    /// <summary>The newest daemon log's final lines, the evidence a failed election leaves.</summary>
    private async Task<string> DaemonLogTailAsync()
    {
        var logDirectory = _fileSystem.Path.Combine(Environment["XDG_DATA_HOME"], "opencode", "log");
        var newest = _fileSystem.Directory.Exists(logDirectory)
            ? _fileSystem.Directory.GetFiles(logDirectory, "*.log").OrderByDescending(_fileSystem.File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (newest is null)
        {
            return "No daemon log under " + logDirectory;
        }

        var lines = (await ReadSharedAsync(newest).ConfigureAwait(false)).Split('\n');
        return "Daemon log tail (" + newest + "):" + System.Environment.NewLine
            + string.Join(System.Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - LogTailLines)));
    }

    private async Task<string> ReadSharedAsync(string path)
    {
        using var stream = _fileSystem.FileStream.New(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task<EnsureServiceContext> CreateAsync(string channelFile, CancellationToken cancellationToken)
    {
        var context = new EnsureServiceContext { _channelFile = channelFile };
        await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return context;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _runRoot = new TestRunRoot(_fileSystem);
        _isolation = ServerIsolation.For(_fileSystem, _runRoot.Path);
        _port = ReservePort();
        _registrationFile = _fileSystem.Path.Combine(
            Environment["XDG_STATE_HOME"], "opencode", _channelFile!);
        await SeedConfigAsync(cancellationToken).ConfigureAwait(false);
        _shimDirectory = _runRoot.CreateSubdirectory("shim");
        _ledgerPath = _fileSystem.Path.Combine(_runRoot.Path, "contenders.ledger");
        _ledger = new ContenderLedger(_fileSystem, _ledgerPath);
        _shimPath = await OpenCodeCommandShim.WriteAsync(_fileSystem, _shimDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static int ReservePort()
    {
        var port = LoopbackPortReservation.Reserve();
        if (port == RealDaemonPort)
        {
            throw new InvalidOperationException(
                $"The reserved loopback port is {RealDaemonPort.ToString(CultureInfo.InvariantCulture)}, the release "
                + "channels' default where the maintainer's real daemon lives; the environment is contaminated. "
                + "Refusing to run rather than touch it.");
        }

        return port;
    }

    private async Task SeedConfigAsync(CancellationToken cancellationToken)
    {
        // The channel's config file, the way `opencode service set port` would have written it:
        // the daemon reads the port from here, so a spawned contender binds this reserved port.
        var directory = Environment["OPENCODE_CONFIG_DIR"];
        _ = _fileSystem.Directory.CreateDirectory(directory);
        var file = _fileSystem.Path.Combine(directory, _channelFile!);
        using var stream = _fileSystem.FileStream.New(file, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = new UTF8Encoding(false).GetBytes("{\"port\":" + _port.ToString(CultureInfo.InvariantCulture) + "}\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
