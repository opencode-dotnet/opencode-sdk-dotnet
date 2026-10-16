using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Owns the normal PTY live test's server-relative location and cleanup. External mode resolves
/// its directory from that server; owned mode creates a workspace under the fixture's run root.
/// </summary>
internal sealed class PtyLiveScenario
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    public PtyFailureDiagnostics Diagnostics { get; } = new();

    private readonly List<Task> _pendingOperations = [];
    private readonly List<PtySession> _sessions = [];
    private readonly PinnedOpenCodeServerFixture _server;
    private OpenCodeClient? _client;
    private string? _directory;
    private LocationSelector? _location;
    private string? _mode;
    private TestWorkspace? _workspace;
    private PtyClient? _terminal;
    private bool _removed;
    private int _cleaned;

    public PtyLiveScenario(PinnedOpenCodeServerFixture server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
    }

    public OpenCodeClient Client =>
        _client ?? throw new InvalidOperationException("The PTY live scenario has not initialized.");

    public string Directory =>
        _directory ?? throw new InvalidOperationException("The PTY live scenario has not initialized.");

    public LocationSelector Location =>
        _location ?? throw new InvalidOperationException("The PTY live scenario has not initialized.");

    public string Mode =>
        _mode ?? throw new InvalidOperationException("The PTY live scenario has not initialized.");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("The PTY live scenario has already initialized.");
        }

        if (_server.IsExternal)
        {
            _client = _server.CreateClient();
            var response = await _client.GetLocationAsync(cancellationToken: cancellationToken);
            _directory = response.ResolvedLocation.Directory;
            if (string.IsNullOrWhiteSpace(_directory))
            {
                throw new InvalidOperationException("The external server returned a blank location directory.");
            }

            _location = new LocationSelector { Directory = _directory };
            _mode = "external";
            return;
        }

        _workspace = _server.CreateWorkspace();
        _directory = _workspace.Path;
        _location = new LocationSelector { Directory = _directory };
        _client = _server.CreateClient(_location);
        _mode = "owned";
    }

    public PtySession Own(PtySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions.Add(session);
        return session;
    }

    public void Own(PtyClient terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        _terminal = terminal;
    }

    public void Observe(Task operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _pendingOperations.Add(operation);
    }

    public void MarkRemoved() => _removed = true;

    /// <summary>
    /// Tears the live PTY down. The calling test names itself, so the failure artifact reports
    /// the test that actually failed rather than whichever one was written first.
    /// </summary>
    /// <param name="primaryFailure">The failure the test body captured, when any.</param>
    /// <param name="testName">The calling test member; supplied by the compiler.</param>
    public async Task CleanupAsync(Exception? primaryFailure, [CallerMemberName] string testName = "")
    {
        if (Interlocked.Exchange(ref _cleaned, 1) is 1)
        {
            ExceptionDispatchInfo.Capture(
                primaryFailure ?? new InvalidOperationException("The PTY live scenario was cleaned twice.")).Throw();
        }

        if (primaryFailure is not null)
        {
            await Diagnostics.CaptureStatusAsync(_server, _location, primaryFailure);
        }

        var cleanup = new OwnedCleanup(CleanupTimeout);
        if (!_removed && _terminal is not null)
        {
            cleanup.Own("PTY removal", async token =>
            {
                var response = await _terminal.RemoveAsync(
                    new PtyRemoveRequest { Location = Location }, OpenCodeRequestOptions.NoThrow, token);
                if (response.Status is not 204 and not 404)
                {
                    throw new InvalidOperationException("PTY cleanup answered HTTP " +
                        response.Status.ToString(CultureInfo.InvariantCulture) + ".");
                }
            });
        }

        foreach (var operation in _pendingOperations)
        {
            cleanup.Own("PTY pending read", operation);
        }

        foreach (var session in _sessions)
        {
            cleanup.Own("PTY socket disposal", async _ => await session.DisposeAsync());
        }

        cleanup.Own("PTY client disposal", _ =>
        {
            _client?.Dispose();
            return Task.CompletedTask;
        });
        cleanup.Own("PTY workspace disposal", _ =>
        {
            _workspace?.Dispose();
            return Task.CompletedTask;
        });
        try
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
        catch (Exception exception)
        {
            _server.MarkFailure(exception, "PtySessionLiveTests." + testName, Diagnostics.Describe());
            throw;
        }
    }
}
