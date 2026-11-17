using System.Runtime.CompilerServices;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>A resolved connection that composes a process-backed handle when its mode has one.</summary>
public class OpenCodeServerConnection : IAsyncDisposable
{
    private readonly OpenCodeServerConnectionMode _mode;
    private readonly Uri? _endpoint;
    private readonly string? _password;
    private readonly string? _serverVersion;
    private readonly OpenCodeServer? _server;
    private readonly OpenCodeServerServiceOperations? _serviceOperations;
    private int _disposed;

    /// <summary>Initializes a mocking instance whose unoverridden members throw an instructive failure.</summary>
    protected OpenCodeServerConnection()
    {
    }

    internal OpenCodeServerConnection(Uri endpoint, string? password, string serverVersion)
    {
        _endpoint = endpoint;
        _password = password;
        _serverVersion = serverVersion;
        _mode = OpenCodeServerConnectionMode.Explicit;
    }

    internal OpenCodeServerConnection(OpenCodeServer server, string? serverVersion)
    {
        _endpoint = server.Endpoint;
        _password = server.Password;
        _server = server;
        _serverVersion = serverVersion;
        _mode = OpenCodeServerConnectionMode.Standalone;
    }

    internal OpenCodeServerConnection(OpenCodeServer server, string? serverVersion, OpenCodeServerServiceOperations serviceOperations)
        : this(server, serverVersion)
    {
        _mode = OpenCodeServerConnectionMode.Service;
        _serviceOperations = serviceOperations;
    }

    /// <summary>Gets how this connection reaches and owns its server.</summary>
    public virtual OpenCodeServerConnectionMode Mode => Read(_mode);

    /// <summary>Gets the resolved endpoint, unchanged by reconnect or restart.</summary>
    public virtual Uri Endpoint => _endpoint ?? throw MockSeam.CreateError(nameof(OpenCodeServerConnection), nameof(Endpoint));

    /// <summary>Gets the pinned server's basic-authentication username, opencode.</summary>
    public virtual string Username => Read("opencode");

    /// <summary>Gets the selected mode's credential, or null for an unauthenticated server.</summary>
    public virtual string? Password => Read(_password);

    /// <summary>Gets the version learned at resolve, or null when none was learned.</summary>
    /// <remarks>Reconnect and restart return new handles and never update this value.</remarks>
    public virtual string? ServerVersion => Read(_serverVersion);

    /// <summary>Gets the owned standalone or shared service handle, or null in explicit mode.</summary>
    public virtual OpenCodeServer? Server => Read(_server);

    /// <summary>Gets reconnect and restart operations in service mode, or null otherwise.</summary>
    public virtual OpenCodeServerServiceOperations? ServiceOperations => Read(_serviceOperations);

    /// <summary>Builds a caller-owned client; Endpoint, Username and Password keep their defaults, and every other option the delegate sets is carried into the client.</summary>
    /// <param name="configure">Optional configuration; Endpoint, Username and Password must keep their defaults.</param>
    /// <returns>A new client bound to this connection.</returns>
    /// <exception cref="InvalidOperationException">The delegate left Endpoint or Password non-null, or Username other than <c>opencode</c>.</exception>
    public virtual OpenCodeClient CreateClient(Action<OpenCodeClientOptions>? configure = null) =>
        ClientIdentityComposer.CreateClient(Endpoint, Password, configure);

    /// <summary>Ends an owned standalone server once; explicit and shared service connections release nothing.</summary>
    /// <returns>A task completing after any owned server has ended.</returns>
    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        GC.SuppressFinalize(this);
        if (_mode == OpenCodeServerConnectionMode.Standalone && _server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private T Read<T>(T value, [CallerMemberName] string member = "") =>
        _endpoint is null ? throw MockSeam.CreateError(nameof(OpenCodeServerConnection), member) : value;
}
