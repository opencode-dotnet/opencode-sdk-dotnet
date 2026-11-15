using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Reconnects or restarts a shared service using the connection's resolve-time inputs.</summary>
/// <remarks>Calls are not serialized. Returned handles do not own the daemon and belong to the caller.</remarks>
public class OpenCodeServerServiceOperations
{
    private readonly ManagedServiceSnapshot? _snapshot;
    private readonly IServerDoors? _doors;

    /// <summary>Initializes a mocking instance whose unoverridden operations throw an instructive failure.</summary>
    protected OpenCodeServerServiceOperations()
    {
    }

    internal OpenCodeServerServiceOperations(ManagedServiceSnapshot snapshot, IServerDoors doors)
    {
        _snapshot = snapshot;
        _doors = doors;
    }

    /// <summary>Ensures the captured service without a version requirement or start announcement.</summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A new non-owning handle, without changing the resolved connection.</returns>
    public virtual Task<OpenCodeServer> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshot ?? throw MockSeam.CreateError(nameof(OpenCodeServerServiceOperations), nameof(ReconnectAsync));
        cancellationToken.ThrowIfCancellationRequested();
        // A real instance initializes both collaborators together.
        return _doors!.EnsureLocatedAsync(snapshot.ReconnectRequest, snapshot.Paths, snapshot.Overlay, cancellationToken);
    }

    /// <summary>Stops with best-effort persistent-terminal handoff, then reconnects to the captured service.</summary>
    /// <param name="cancellationToken">The caller's token; a cancelled call never starts stopping.</param>
    /// <returns>A new non-owning handle, without changing the resolved connection.</returns>
    public virtual async Task<OpenCodeServer> RestartAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshot ?? throw MockSeam.CreateError(nameof(OpenCodeServerServiceOperations), nameof(RestartAsync));
        cancellationToken.ThrowIfCancellationRequested();
        // A real instance initializes both collaborators together.
        await _doors!.StopLocatedAsync(snapshot.Paths, OpenCodeServerStopPtyMode.Handoff, cancellationToken).ConfigureAwait(false);
        return await _doors.EnsureLocatedAsync(snapshot.ReconnectRequest, snapshot.Paths, snapshot.Overlay, cancellationToken).ConfigureAwait(false);
    }
}
