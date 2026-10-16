using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>
/// Starts the launcher against a cooperating Bun process tree and owns the startup and bounded
/// cleanup around the handshake's independently owned control leases.
/// </summary>
internal sealed class ServerStartupTreeScenario : IAsyncDisposable
{
    private const string InvalidLineMode = "invalid-line";
    private const string SilentMode = "silent";

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The bound inside which the already-observed descendant must terminate after the startup
    /// result. A test observation bound of its own: it neither shares nor extends the launcher's
    /// forced-exit budget, and no test-side signal, lease release, retry, or sleep can satisfy it.
    /// </summary>
    private static readonly TimeSpan DescendantTerminationBound = TimeSpan.FromSeconds(10);

    private readonly ServerStartupTreeHandshake _handshake = new(new RealFileSystem());
    private readonly string _mode;
    private readonly TimeSpan _readinessTimeout;
    private readonly bool _acknowledge;
    private Task<OpenCodeServer>? _startup;
    private Task? _observation;
    private CancellationTokenSource? _startupCancellation;
    private readonly List<Exception> _cleanupFailures = [];
    private string? _diagnosticEvidence;
    private int _disposed;

    private ServerStartupTreeScenario(
        string mode,
        TimeSpan readinessTimeout,
        bool acknowledge)
    {
        _mode = mode;
        _readinessTimeout = readinessTimeout;
        _acknowledge = acknowledge;
    }

    public Process ChildProcess => _handshake.ChildProcess;

    /// <summary>The evidence gathered when the descendant survived its bound; empty otherwise.</summary>
    public string DescendantEvidence { get; private set; } = string.Empty;

    public Process RootProcess => _handshake.RootProcess;

    /// <summary>
    /// Composes inert scenario state. Process/network work starts through StartAndObserveAsync.
    /// </summary>
    public static ServerStartupTreeScenario CreateInvalidLine() =>
        new(InvalidLineMode, TimeSpan.FromMinutes(2), acknowledge: true);

    public static ServerStartupTreeScenario CreateHandshakeFailure() =>
        new(SilentMode, TimeSpan.FromMinutes(2), acknowledge: false);

    public static ServerStartupTreeScenario CreateSilent(TimeSpan readinessTimeout) =>
        new(SilentMode, readinessTimeout, acknowledge: true);

    public async Task StartAndObserveAsync(CancellationToken cancellationToken)
    {
        _startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _handshake.Start();
        _observation = _handshake.CaptureAndAcknowledgeAsync(_acknowledge, _startupCancellation.Token);
        _startup = OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", new FixtureLoader().LoadText("Server.child-tree.js")],
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OPENCODE_SDK_TEST_TREE_PORT"] = _handshake.Port.ToString(CultureInfo.InvariantCulture),
                    ["OPENCODE_SDK_TEST_TREE_NONCE"] = _handshake.Nonce,
                    ["OPENCODE_SDK_TEST_TREE_MODE"] = _mode,
                },
                ReadinessTimeout = _readinessTimeout,
            },
            _startupCancellation.Token);
        var observation = _observation;
        await observation.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Bounded descendant termination evidence with both leases still held: waits at most
    /// <see cref="DescendantTerminationBound"/> for the observed grandchild to exit. A whole-tree
    /// kill is asynchronous, so the direct process exiting proves nothing about this child at that
    /// instant (client-runtime, Launcher); this never proves every descendant reaped at return.
    /// </summary>
    /// <returns>True when the descendant exited inside the bound; false with <see cref="DescendantEvidence"/> set.</returns>
    public async Task<bool> ObserveDescendantTerminationAsync(CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(DescendantTerminationBound);
        try
        {
            await _handshake.WaitForChildExitAsync(bound.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DescendantEvidence = await DescribeProcessesAsync();
            return false;
        }
    }

    public void ReleaseChildLease() => _handshake.ReleaseChild();

    public void ReleaseRootLease() => _handshake.ReleaseRoot();

    public async Task<string> DescribeProcessesAsync()
    {
        using var diagnosticBound = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _diagnosticEvidence = await _handshake.DescribeProcessesAsync(diagnosticBound.Token);
        return _diagnosticEvidence;
    }

    public async Task<OpenCodeServer> WaitForStartupAsync(CancellationToken cancellationToken)
    {
        var startup = _startup ?? throw new InvalidOperationException("The scenario has not been started.");
        return await startup.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 1)
        {
            return;
        }

        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            // Close both owned connections even if pending observation failed. Releasing these
            // leases is fallback cleanup and only happens after the test's immediate assertions.
            CaptureFailure(_handshake.ReleaseChild);
            CaptureFailure(_handshake.ReleaseRoot);
            if (_startupCancellation is { } startupCancellation)
            {
                await CaptureFailureAsync(startupCancellation.CancelOnWorkerAsync);
            }

            CaptureFailure(_handshake.Stop);
            await CaptureFailureAsync(() => AwaitObservationCleanupAsync(cleanup.Token));
            await CaptureFailureAsync(
                () => AwaitStartupCleanupAsync(cleanup.Token));
            await CaptureFailureAsync(
                () => _handshake.WaitForChildExitAsync(cleanup.Token));
            await CaptureFailureAsync(
                () => _handshake.WaitForRootExitAsync(cleanup.Token));
        }
        finally
        {
            try
            {
                _handshake.Dispose();
            }
            catch (Exception exception)
            {
                _cleanupFailures.Add(exception);
            }

            try
            {
                _startupCancellation?.Dispose();
            }
            catch (Exception exception)
            {
                _cleanupFailures.Add(exception);
            }
        }

        ThrowCleanupFailures(_cleanupFailures);
    }

    private void CaptureFailure(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            _cleanupFailures.Add(exception);
        }
    }

    private async Task CaptureFailureAsync(Func<Task> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            _cleanupFailures.Add(exception);
        }
    }

    private void ThrowCleanupFailures(List<Exception> failures)
    {
        if (failures.Count is 0)
        {
            return;
        }

        if (_diagnosticEvidence is { } evidence)
        {
            throw new AggregateException(
                "Startup tree cleanup failed after an exit observation failed. " + evidence, failures);
        }

        if (failures.Count is 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
            return;
        }

        throw new AggregateException("Multiple failures occurred while cleaning the startup tree scenario.", failures);
    }

    private async Task AwaitStartupCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startup = _startup;
            if (startup is null)
            {
                return;
            }

            var server = await startup.WaitAsync(cancellationToken);
            await server.DisposeAsync();
        }
        catch (OperationCanceledException exception) when (_startup?.IsCanceled is true)
        {
            _ = exception;
        }
        catch (OpenCodeServerException exception)
        {
            _ = exception;
        }
    }

    private async Task AwaitObservationCleanupAsync(CancellationToken cancellationToken)
    {
        var observation = _observation;
        if (observation is not null)
        {
            await observation.WaitAsync(cancellationToken);
        }
    }
}
