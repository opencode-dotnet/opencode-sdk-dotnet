using System.Runtime.ExceptionServices;
using OpenCode.Sdk.TestSupport.Abstractions;
using OpenCode.Sdk.TestSupport.Ownership;
using Testably.Abstractions;
using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The exact-pin server fixture in simulation mode (design §7.4): a named drive instance with a
/// per-run manifest (never the fixed default ports), a config-seeded simulated provider, and an
/// attached drive controller. Simulation denies all unregistered outbound network by
/// construction (backend/index.ts:29-35), so the workflow runs with no provider credentials.
/// </summary>
public sealed class SimulatedDriveServerFixture : IAsyncInitializer, IAsyncDisposable, ITestEndEventReceiver
{
    private static readonly TimeSpan ControllerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The launcher's worst case is the 10-second grace, its 10-second forced-exit wait, and the
    /// 2-second output drain; this outer bound keeps a 3-second margin above that.
    /// </summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(25);

    private readonly RealFileSystem _fileSystem = new();
    private readonly IGitProcess _gitProcess = new GitProcess();
    private OpenCodeServer? _server;
    private OpenCodeServerOutput? _output;
    private DriveController? _controller;
    private TestRunRoot? _runRoot;
    private LocationSelector? _defaultLocation;
    private bool _retainLogs;
    private ServerFailureArtifacts? _artifacts;
    private int _disposed;
    public Uri Endpoint => Server.Endpoint;

    public int Order => 0;

    internal DriveController Controller =>
        _controller ?? throw new InvalidOperationException("The fixture has not initialized.");

    internal OpenCodeServer Server =>
        _server ?? throw new InvalidOperationException("The fixture has not initialized.");

    private LocationSelector DefaultLocation =>
        _defaultLocation ?? throw new InvalidOperationException("The fixture has not initialized.");

    internal TestRunRoot RunRoot =>
        _runRoot ?? throw new InvalidOperationException("The fixture has not initialized.");

    public async Task InitializeAsync()
    {
        _runRoot = new TestRunRoot(_fileSystem);
        _defaultLocation = new LocationSelector
        {
            Directory = _runRoot.CreateSubdirectory(PinnedOpenCodeServerFixture.DefaultLocationName),
        };
        try
        {
            _controller = await StartAsync(_runRoot);
            await _controller.HandshakeAsync();
            await _controller.AttachAsync();
        }
        catch (Exception exception)
        {
            _retainLogs = true;
            MarkFailure(exception, "fixture initialization", "phase=server startup");
            throw;
        }
    }

    /// <summary>
    /// Brings up the simulated server and its attached-ready control socket. The whole
    /// reserve-manifest-through-bound-socket span runs under the
    /// <see cref="MachineLock.DrivePorts"/> lock: the manifest must name explicit ports the
    /// server binds later (manifest.ts:12-21), so without the lock two concurrently starting test
    /// hosts can be handed the same loopback port and the loser dies before readiness.
    /// </summary>
    private async Task<DriveController> StartAsync(TestRunRoot runRoot)
    {
        using var gate = await MachineLock.AcquireAsync(_fileSystem, MachineLock.DrivePorts, SimulatedServerLaunch.GateTimeout);
        var launch = SimulatedServerLaunch.Prepare(_fileSystem, runRoot);

        // The collector exists before the start and stays readable when the start fails, so a
        // startup failure still has stdout/stderr to write out on teardown.
        _output = new OpenCodeServerOutput();
        _server = await launch.StartAsync(_output, CancellationToken.None);
        var manifest = launch.Manifest;

        // The backend control socket is already listening when the readiness line is printed -
        // simulation builds the network layer eagerly at server start (backend/index.ts,
        // simulated-provider.ts:274-286) - so this connects once and fails the whole fixture
        // loudly if it cannot, rather than retrying blind. A successful connect is also what
        // proves the reserved port is now bound, which is what lets the gate above be released.
        return await DriveController.ConnectAsync(manifest.BackendEndpoint, ControllerTimeout);
    }

    /// <summary>A client on <paramref name="location"/>, or on the fixture's owned default location (<see cref="PinnedOpenCodeServerFixture.DefaultLocationName"/>).</summary>
    public OpenCodeClient CreateClient(LocationSelector? location = null) =>
        new(new OpenCodeClientOptions
        {
            Endpoint = Server.Endpoint,
            Password = Server.Password,
            Location = location ?? DefaultLocation,
        });

    internal async Task<string> ReadGlobalConfigAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = _fileSystem.File.OpenText(
            _fileSystem.Path.Combine(RunRoot.Path, "config", "opencode", "opencode.jsonc"));
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public TestWorkspace CreateWorkspace() => new(_fileSystem, RunRoot.Path);

    public Task<GitRepositoryWorkspace> CreateGitRepositoryWorkspaceAsync(CancellationToken cancellationToken) =>
        GitRepositoryWorkspace.CreateAsync(_fileSystem, _gitProcess, RunRoot.Path, cancellationToken);

    private ServerFailureArtifacts Artifacts => _artifacts ??= new ServerFailureArtifacts(
        _fileSystem, new TestResultsDirectory(_fileSystem).Resolve(Environment.GetCommandLineArgs()));

    private void MarkFailure(Exception exception, string test, string details)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _ = Artifacts.Mark(exception, test, details, TestContext.Current?.Id);
    }

    public ValueTask OnTestEnd(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Execution.Result?.Exception is { } failure)
        {
            var path = Artifacts.Mark(
                failure,
                context.Metadata.TestDetails.ClassType.FullName + "." + context.Metadata.TestName,
                "phase=test-body; receiver observes the body result, before later teardown outcomes",
                context.Id);
            if (Artifacts.Report(failure, context.Id))
            {
                context.Output.AttachArtifact(path, "Owned simulated server failure diagnostics");
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
        var teardown = new OwnedCleanup(TeardownTimeout);
        if (_controller is not null)
        {
            teardown.Own("simulation controller teardown", async _ => await _controller.DisposeAsync());
        }

        if (_server is not null && _output is not null)
        {
            teardown.Own("simulated server teardown", async _ => await _server.DisposeAsync());

            // Runs after the teardown above has settled, so the collector is final.
            teardown.Own("simulated server diagnostic contract", _ =>
            {
                EnsureDiagnosticContract(_server, _output);
                return Task.CompletedTask;
            });
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

        if (failure is not null || keep)
        {
            if (failure is not null && !Artifacts.Failures.Any(item => ReferenceEquals(item.Exception, failure)))
            {
                _ = Artifacts.Mark(failure, "fixture disposal", "phase=owned-server-teardown");
            }

            var capture = new ServerFailureCapture(
                Artifacts, _fileSystem, new OwnedOperationDeadline());
            failure = await capture.CaptureAsync(_output, _server?.ProcessId, external: false, failure, teardown.OperationFailures);
            Console.WriteLine("Simulated server diagnostics: " + Artifacts.Directory);
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

    private bool ShouldRetainLogs => _retainLogs || _artifacts?.Failures.Count > 0 || string.Equals(
        Environment.GetEnvironmentVariable("OPENCODE_SDK_TESTS_KEEP_LOGS"), "1", StringComparison.Ordinal);

    /// <summary>
    /// The shared instance proves what its final snapshot can still hold. On Windows the host logs
    /// "stdin closed" once the launcher releases the lease, before the collection closes, so it is
    /// the last stderr line and survives the collector's bound. On Linux and macOS <c>SIGTERM</c>
    /// ends the host and the lease is released last, so the host writes no "stdin closed" line,
    /// and the proof is that the host's diagnostics reached stderr at all: a line carrying its
    /// severity. The earlier "starting"/"ready" milestones are evicted over a chatty session (INFO
    /// logging retains the newest 500 lines); they are proven per lifecycle by
    /// <c>PersistentSimulationHostTests</c>, which starts its own short-lived host and reads a
    /// snapshot that lost nothing. This is the explicit migration reduction from the retired
    /// adapter's push-based startup capture, not a claim about every shared instance.
    /// </summary>
    private static void EnsureDiagnosticContract(OpenCodeServer server, OpenCodeServerOutput output)
    {
        var snapshot = output.GetSnapshot();
        var milestone = OperatingSystem.IsWindows() ? "persistent simulation host stdin closed" : "level=";
        if (!snapshot.StandardError.Any(line => line.Contains(milestone, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The persistent simulation host did not retain the diagnostic '" + milestone +
                "' (stderr truncated: " + snapshot.StandardErrorTruncated + ").");
        }

        Console.WriteLine(
            "Persistent simulation host retained the diagnostic '" + milestone + "' for process " +
            server.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " (stderr truncated: " + snapshot.StandardErrorTruncated + ").");
    }
}
