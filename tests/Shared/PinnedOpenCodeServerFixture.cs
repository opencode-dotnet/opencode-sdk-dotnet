using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport.Ownership;
using Testably.Abstractions;
using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The exact-pin server fixture (design §7.2): one pinned server per test session started
/// through the SDK's own <see cref="OpenCodeServer"/> launcher with its output collector (the
/// launcher is dogfooded by every live test), per-run home/state isolation, isolated
/// workspaces, and logs retained on failure. Consumers declare
/// <c>[ClassDataSource&lt;PinnedOpenCodeServerFixture&gt;(Shared = SharedType.PerTestSession)]</c>;
/// every consumer adds <c>[NotInParallel(ParallelConstraintKeys.ServerProcess)]</c>, the key
/// every server-process test shares.
/// Fail-fast, never skip: a missing submodule/install/bun surfaces as an instructive error.
/// </summary>
/// <remarks>
/// Environment variables this fixture and its neighbors respond to, one line each:
/// <list type="bullet">
/// <item><c>OPENCODE_SDK_TESTS_KEEP_LOGS=1</c> - retain the spawned server's stdout/stderr (and
/// the run root); bounded log files are written beneath the runner's results directory.</item>
/// <item><c>OPENCODE_SDK_TESTS_ENDPOINT</c> - an operator-supplied server to attach to instead of
/// spawning one (paired with <c>OPENCODE_SDK_TESTS_PASSWORD</c>; see below).</item>
/// <item><c>OPENCODE_SDK_TESTS_PASSWORD</c> - the Basic-auth password for
/// <c>OPENCODE_SDK_TESTS_ENDPOINT</c>; both or neither, never one alone.</item>
/// <item><c>OPENCODE_SDK_TESTS_SERVER_COMMAND</c> - a <c>|</c>-separated command the owned mode
/// starts instead of the pinned source run (for example <c>opencode|serve</c>); everything else
/// about the owned path is unchanged, so the same suite runs against another build of the same
/// server (see <see cref="PinnedServerCommandOverride"/>).</item>
/// <item><c>OPENCODE_SDK_TESTS_PTY_DAEMON=0|1</c> - overrides <see cref="PersistentPtyDaemonGate"/>'s
/// platform default (see its own remarks).</item>
/// </list>
/// External-endpoint mode (Task 6, the WSL2 recipe - <c>docs/engineering/developing-on-windows.md</c>
/// carries the runnable steps): when <c>OPENCODE_SDK_TESTS_ENDPOINT</c>/
/// <c>OPENCODE_SDK_TESTS_PASSWORD</c> name an operator-supplied server - or the internal
/// <see cref="ExternalServerEndpoint"/> constructor supplies the pair directly -
/// <see cref="InitializeAsync"/> spawns nothing: it probes the server's health instead of
/// starting an owned server, so <see cref="Server"/> stays unset and every member below reads
/// from the external pair. The owned RPC plugin is the one thing such a server does not carry,
/// so <see cref="RpcPlugin"/> - not <see cref="IsExternal"/> - is what the RPC-dependent proofs
/// branch on.
/// </remarks>
public sealed class PinnedOpenCodeServerFixture : IAsyncInitializer, IAsyncDisposable, ITestEndEventReceiver
{
    private static readonly TimeSpan ExternalHealthProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The location a client gets when its test names none. The pinned server resolves a request
    /// without a directory to its own working directory (<c>packages/server/src/location.ts:43</c>
    /// at the pin), which bun needs anchored inside the upstream checkout - so a location-less
    /// request would run against upstream's own development project. An owned, empty directory
    /// keeps those requests inside the fixture.
    /// </summary>
    internal const string DefaultLocationName = "default-location";

    /// <summary>
    /// The launcher's worst case is the 10-second grace, its 10-second forced-exit wait, and the
    /// 2-second output drain; this outer bound keeps a 3-second margin above that.
    /// </summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(25);

    private readonly RealFileSystem _fileSystem = new();
    private readonly bool _forceOwned;
    private readonly IReadOnlyList<string>? _commandOverride;
    private readonly string? _workingDirectoryOverride;
    private readonly ExternalServerEndpoint? _externalOverride;
    private OpenCodeServer? _server;
    private OpenCodeServerOutput? _output;
    private TestRunRoot? _runRoot;
    private LocationSelector? _defaultLocation;
    private ExternalServerEndpoint? _external;
    private string? _commandSource;
    private bool _retainLogs;
    private bool _externalMode;
    private ServerFailureArtifacts? _artifacts;
    private readonly ServerFixtureDiagnosticsOptions? _diagnostics;
    private int _disposed;
    private readonly List<LateCleanupFailureReport> _lateReports = [];

    public PinnedOpenCodeServerFixture()
    {
    }

    internal PinnedOpenCodeServerFixture(bool forceOwned)
    {
        _forceOwned = forceOwned;
    }

    /// <summary>
    /// Test-only seam: drives <see cref="InitializeAsync"/>/<see cref="DisposeAsync"/> against an
    /// injected command instead of the real pinned server, so a deliberate startup failure can be
    /// forced through this fixture itself - pinning the failure-path log-retention contract rather
    /// than only exercising it through the lower-level adapter.
    /// </summary>
    internal PinnedOpenCodeServerFixture(IReadOnlyList<string> command, string workingDirectory, ServerFixtureDiagnosticsOptions? diagnostics = null)
    {
        _diagnostics = diagnostics;
        _commandOverride = command;
        _workingDirectoryOverride = workingDirectory;
    }

    /// <summary>
    /// Test-only seam: drives <see cref="InitializeAsync"/> against an injected external endpoint
    /// instead of resolving <see cref="ExternalServerEndpoint.FromEnvironment()"/> from the real
    /// process environment, so the attach/refuse contract is testable over a loopback server.
    /// </summary>
    internal PinnedOpenCodeServerFixture(ExternalServerEndpoint external)
    {
        ArgumentNullException.ThrowIfNull(external);

        _externalOverride = external;
    }

    public Uri Endpoint => _external?.Endpoint ?? Server.Endpoint;

    public int Order => 0;

    internal bool IsExternal
    {
        get
        {
            if (_external is not null)
            {
                return true;
            }

            if (_server is null)
            {
                throw new InvalidOperationException("The fixture has not initialized.");
            }

            return false;
        }
    }

    /// <summary>
    /// The repository-owned RPC plugin this session's server carries: the copy every owned server
    /// is seeded with - the pinned source run and an <c>OPENCODE_SDK_TESTS_SERVER_COMMAND</c> run
    /// alike - or <see langword="null"/> for an external endpoint, which the fixture does not
    /// configure. The RPC and event proofs branch on this rather than on <see cref="IsExternal"/>,
    /// because what they need is the plugin, not the ownership.
    /// </summary>
    internal TestRpcPlugin? RpcPlugin { get; private set; }

    internal OpenCodeServer Server =>
        _server ?? throw new InvalidOperationException("The fixture has not initialized.");

    internal TestRunRoot RunRoot =>
        _runRoot ?? throw new InvalidOperationException("The fixture has not initialized.");

    public async Task InitializeAsync()
    {
        _runRoot = new TestRunRoot(_fileSystem);

        // The command-override constructor forces a deliberate local-spawn failure
        // (PinnedOpenCodeServerFixtureFailureTests); ambient OPENCODE_SDK_TESTS_ENDPOINT/PASSWORD
        // left over from an operator's WSL2 recipe session must never hijack that test into
        // attaching to a real server instead, so the environment fallback is only consulted for
        // the two modes that do not already name a fixed mode of their own.
        if (!_forceOwned && (_commandOverride is null || _workingDirectoryOverride is null))
        {
            var external = _externalOverride ?? ExternalServerEndpoint.FromEnvironment();
            if (external is not null)
            {
                _externalMode = true;
                await AttachToExternalAsync(external).ConfigureAwait(false);
                return;
            }
        }

        IReadOnlyList<string> command;
        string workingDirectory;
        bool startsOpenCode;
        if (_commandOverride is not null && _workingDirectoryOverride is not null)
        {
            // The constructor seam the failure tests use: a stand-in command, not an opencode
            // server, so there is no database whose place could prove the isolation.
            command = _commandOverride;
            workingDirectory = _workingDirectoryOverride;
            startsOpenCode = false;
        }
        else
        {
            var pinnedCommand = new PinnedServerCommand(_fileSystem);

            // The override replaces the command and nothing else: the isolated roots, the seeded
            // RPC plugin, the launcher's own readiness and teardown, and the retained logs are all
            // still the owned path, which is what makes another build of the same server
            // answerable by this suite unchanged. Resolve() is not called when the override is set
            // - it validates the submodule checkout the override deliberately does not use.
            var environmentCommand = PinnedServerCommandOverride.FromEnvironment();
            command = environmentCommand?.Command ?? pinnedCommand.Resolve();
            _commandSource = environmentCommand is null
                ? "owned (pinned source)"
                : "owned (OPENCODE_SDK_TESTS_SERVER_COMMAND)";
            RpcPlugin = new TestRpcPlugin(_fileSystem, pinnedCommand.RepositoryRoot);

            // Bun's workspace/tsconfig discovery for the pinned monorepo's JSX packages walks
            // from the process's working directory, not from the absolute entry-file path (Task
            // 2's confirmed repro, OpenCodeServerLifecycleTests.StartPinnedAsync): a scratch
            // directory outside the checkout leaves that discovery unable to find the workspace
            // root, and the source-run server fails before readiness with "Cannot find module
            // 'react/jsx-dev-runtime'". Anchoring at the CLI package is what upstream's own "dev"
            // script does; state/data/cache/config stay isolated through the environment below
            // regardless of this directory.
            workingDirectory = _fileSystem.Path.Combine(
                pinnedCommand.RepositoryRoot, "external", "opencode", "packages", "cli");
            startsOpenCode = true;
        }

        await StartOwnedServerAsync(command, workingDirectory, startsOpenCode).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the owned server through the SDK's own launcher and names the build it started. An
    /// opencode server is confirmed to have honoured its isolation before the fixture hands it out.
    /// </summary>
    private async Task StartOwnedServerAsync(IReadOnlyList<string> command, string workingDirectory, bool startsOpenCode)
    {
        // An external endpoint may not share this machine's filesystem, so only an owned server
        // gets a default location.
        _defaultLocation = new LocationSelector { Directory = RunRoot.CreateSubdirectory(DefaultLocationName) };

        // The collector exists before the start and stays readable when the start fails, so a
        // startup failure still has stdout/stderr to write out on teardown.
        _output = new OpenCodeServerOutput();
        var isolation = BuildIsolation(RunRoot.Path);
        try
        {
            _server = await OpenCodeServer.StartAsync(
                new OpenCodeServerOptions
                {
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    Environment = isolation.Environment,
                    ReadinessTimeout = OwnedServerPolicy.ReadinessTimeout,
                    GracefulShutdownTimeout = OwnedServerPolicy.GracefulShutdownTimeout,
                    Output = _output,
                }).ConfigureAwait(false);
            if (startsOpenCode)
            {
                isolation.ConfirmHonored("The pinned fixture's " + _commandSource + " server");
            }
        }
        catch (Exception exception)
        {
            _retainLogs = true;
            MarkFailure(exception, "fixture initialization", "phase=server startup");
            throw;
        }

        // Which server the session actually reached. The external mode prints its own banner for
        // the same reason: a run's evidence must name the build it was produced against, and an
        // overridden command is the one owned case the pinned commit does not describe.
        if (_commandSource is { } source)
        {
            Console.WriteLine(
                $"Pinned server started: {source} (command: {string.Join(' ', command)}; endpoint: {Server.Endpoint}).");
        }
    }

    public OpenCodeClient CreateClient(LocationSelector? location = null) =>
        new(new OpenCodeClientOptions
        {
            Endpoint = _external?.Endpoint ?? Server.Endpoint,
            Password = _external?.Password ?? Server.Password,
            Location = location ?? _defaultLocation,
        });

    public TestWorkspace CreateWorkspace() => new(_fileSystem, RunRoot.Path);

    /// <summary>
    /// The body location for a session whose test names no workspace. Session creation without a
    /// parent takes its location from the body alone and otherwise binds the session to the server's
    /// own working directory (<c>packages/server/src/handlers/session.ts:136-138</c> at the pin),
    /// whatever the location header says. Absent for an external endpoint, which may not share this
    /// machine's filesystem.
    /// </summary>
    internal Optional<LocationPublicRef?> DefaultSessionLocation
    {
        get
        {
            if (_defaultLocation is { Directory: { } directory })
            {
                return new LocationPublicRef { Directory = directory };
            }

            return default;
        }
    }

    internal string DiagnosticsDirectory => Artifacts.Directory;

    internal async Task DrainDiagnosticsAsync(CancellationToken cancellationToken)
    {
        foreach (var report in _lateReports)
        {
            await report.WaitForAllAsync(cancellationToken);
        }
    }

    private ServerFailureArtifacts Artifacts => _artifacts ??= new ServerFailureArtifacts(
        _diagnostics?.FileSystem ?? _fileSystem,
        _diagnostics?.ResultsDirectory ?? new TestResultsDirectory(_fileSystem).Resolve(Environment.GetCommandLineArgs()));

    internal void MarkFailure(Exception exception, string test, string details)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _ = Artifacts.Mark(exception, test, details, TestContext.Current?.Id);
    }

    public ValueTask OnTestEnd(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Execution.Result?.Exception is { } failure)
        {
            var path = Artifacts.Mark(failure,
                context.Metadata.TestDetails.ClassType.FullName + "." + context.Metadata.TestName,
                "phase=test-body; receiver observes the body result, before later teardown outcomes", context.Id);
            if (Artifacts.Report(failure, context.Id))
            {
                context.Output.AttachArtifact(path, "Owned server failure diagnostics");
            }
        }

        return default;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 1)
        {
            return;
        }

        var keep = ShouldRetainLogs;
        var recorded = _artifacts?.Failures;
        var primary = recorded is { Count: > 0 } ? recorded[0].Exception : null;
        var teardown = new OwnedCleanup(TeardownTimeout,
            _diagnostics?.Deadline ?? new OwnedOperationDeadline());
        if (_server is not null)
        {
            teardown.Own("pinned server teardown", async _ => await _server.DisposeAsync());
        }

        Exception? failure = null;
        try
        {
            await teardown.CompleteAsync(primary);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (teardown.LateFailures is { } late)
        {
            _lateReports.Add(late);
        }

        if (failure is not null || keep)
        {
            if (failure is not null && !Artifacts.Failures.Any(item => ReferenceEquals(item.Exception, failure)))
            {
                _ = Artifacts.Mark(failure, "fixture disposal", "phase=owned-server-teardown");
            }

            var capture = new ServerFailureCapture(Artifacts, _diagnostics?.FileSystem ?? _fileSystem,
                _diagnostics?.Deadline ?? new OwnedOperationDeadline());
            failure = await capture.CaptureAsync(_output, _server?.ProcessId, _externalMode, failure, teardown.OperationFailures);
            _lateReports.AddRange(capture.LateReports);
            Console.WriteLine("Pinned server diagnostics: " + Artifacts.Directory);
        }

        if (failure is null && !keep)
        {
            _runRoot?.Dispose();
        }
        if (failure is not null && !(_artifacts?.IsReported(failure) ?? false))
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
    private bool ShouldRetainLogs => _retainLogs || _diagnostics?.RetainLogs is true || string.Equals(
        Environment.GetEnvironmentVariable("OPENCODE_SDK_TESTS_KEEP_LOGS"), "1", StringComparison.Ordinal);

    private IsolationBoundary BuildIsolation(string runRoot)
    {
        var isolation = ServerIsolation.For(_fileSystem, runRoot);
        if (RpcPlugin is { } plugin)
        {
            isolation.Environment["OPENCODE_CONFIG_CONTENT"] = new ServerConfigSeed()
                .WithPluginDirectory(plugin.Directory)
                .Render();
        }

        return isolation;
    }

    /// <summary>
    /// Probes the external pair's health through a throw-away client and, once it answers, prints
    /// its reported version beside the pinned submodule commit - the exact-pin discipline in this
    /// mode is the operator's, and a source run's version cannot be verified mechanically, so this
    /// is the only mechanical evidence available that the pair actually matches.
    /// </summary>
    private async Task AttachToExternalAsync(ExternalServerEndpoint external)
    {
        using var probeTimeout = new CancellationTokenSource(ExternalHealthProbeTimeout);

        ServerInfoResponse health;
        try
        {
            // OpenCodeClient construction lives inside this try, not before it: Pipeline's own
            // option guards (a missing endpoint, a blank username) throw ArgumentException, and a
            // failure there is exactly as much an external-mode attach failure as the probe
            // itself - it must surface through the same fixture InvalidOperationException naming
            // the endpoint, never as a raw ArgumentException naming "options".
            using var client = new OpenCodeClient(new OpenCodeClientOptions
            {
                Endpoint = external.Endpoint,
                Password = external.Password,
            });
            health = await client.Server.GetInfoAsync(cancellationToken: probeTimeout.Token).ConfigureAwait(false);
        }
        catch (OpenCodeApiException apiException)
        {
            // The server answered - with an error - so this is never a timeout. Surfacing "did
            // not answer within Ns" here would send an operator chasing a nonexistent timeout
            // instead of the real cause (wrong password, broken server).
            throw new InvalidOperationException(
                $"The external server at '{external.Endpoint}' answered the health probe with HTTP " +
                $"{apiException.Status.ToString(CultureInfo.InvariantCulture)} " +
                $"({DescribeApiFailure(apiException)}).",
                apiException);
        }
        catch (OperationCanceledException exception) when (probeTimeout.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"The external server at '{external.Endpoint}' did not answer a health probe within " +
                $"{ExternalHealthProbeTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.",
                exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The external server at '{external.Endpoint}' could not be reached for a health probe: " +
                $"{exception.Message}",
                exception);
        }

        var upstreamCommit = ReadPinnedUpstreamCommit();
        Console.WriteLine(
            $"Attached to external server at '{external.Endpoint}' (reported version: " +
            $"{health.ServerInfo.Version}; pinned upstream commit: {upstreamCommit}). A source run's version " +
            "cannot be verified mechanically.");

        _external = external;
    }

    /// <summary>Describes a failed health probe's typed error, or its raw body when there is none.</summary>
    private static string DescribeApiFailure(OpenCodeApiException apiException) =>
        // The pattern (rather than a string.IsNullOrEmpty call) is what narrows rawBody to
        // non-null on every TFM: net472's older BCL surface does not carry the NotNullWhen
        // attribute IsNullOrEmpty relies on for flow analysis elsewhere (PinnedServerCommand's
        // FindRepositoryRoot records the same fix for the same reason).
        apiException.Error?.Tag ?? (apiException.RawBody is { Length: > 0 } rawBody ? rawBody : "no error body");

    private string ReadPinnedUpstreamCommit()
    {
        var repositoryRoot = new PinnedServerCommand(_fileSystem).RepositoryRoot;
        var receiptPath = _fileSystem.Path.Combine(repositoryRoot, "spec", "receipt.json");
        try
        {
            var receipt = _fileSystem.File.ReadAllText(receiptPath);
            using var document = JsonDocument.Parse(receipt);
            return document.RootElement.GetProperty("upstreamCommit").GetString()
                ?? throw new InvalidOperationException($"'{receiptPath}' has no 'upstreamCommit' value.");
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            // The banner is evidence, and a receipt this run cannot read is still an attach
            // failure: it surfaces named, the way every other external-mode failure above does,
            // rather than as a raw file or JSON fault escaping from behind a successful probe.
            throw new InvalidOperationException(
                $"The pinned upstream commit could not be read from '{receiptPath}': {exception.Message}",
                exception);
        }
    }
}
