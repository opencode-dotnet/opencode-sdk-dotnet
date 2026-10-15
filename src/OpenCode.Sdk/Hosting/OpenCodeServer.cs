using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// A local opencode server reached through one of its process-backed modes, with the ownership
/// the mode implies visible through <see cref="OwnsProcess"/>. <see cref="StartAsync"/> returns
/// an owned standalone server: started on port zero with its own generated lease credential, held
/// through an open stdin pipe, and ended by disposal within bounds. On Windows the server runs in
/// a job that ends it with its owner, however the owner ends, and disposal ends it and its process
/// tree at once with <c>taskkill /T /F</c>, running the same kill again when the server outlives the
/// grace. On Linux and macOS the server runs in a session of its own, out of reach of the host
/// terminal's Ctrl+C and hangup, and disposal sends <c>SIGTERM</c> to its process group, waits a
/// grace, then sends <c>SIGKILL</c> to what is left of the group; when the owner crashes before
/// disposal runs, the operating system closes the lease, and the server exits on that
/// end-of-stream. <see cref="DiscoverAsync"/> returns a
/// shared registered background service the first-party CLI runs for every client on the machine:
/// nothing here owns it, and disposing the handle is a no-op; <see cref="EnsureAsync"/> reuses or
/// starts that shared service and returns the same non-owning handle; the static
/// <see cref="StopAsync"/> is the one deliberate way to end it. Either way the handle carries the
/// endpoint and credential a client needs, and <see cref="CreateClient"/> is the door to them.
/// </summary>
public partial class OpenCodeServer : IAsyncDisposable
{
    private readonly OwnedServerChild? _child;
    private readonly Task<OpenCodeServerExit>? _exited;
    private readonly Uri? _endpoint;
    private readonly string? _password;
    private readonly int? _processId;
    private int _disposed;

    private OpenCodeServer(OwnedServerChild child, Uri endpoint, string password)
    {
        _child = child;
        _endpoint = endpoint;
        _password = password;

        // Captured while the child is live: the identity stays readable for diagnostics after
        // disposal has released the process handle.
        _processId = child.ProcessId;
        _exited = TranslateAsync(child.Closed);
    }

    /// <summary>
    /// Initializes a processless instance for deterministic contract tests; friend-assembly
    /// test seam. Disposal has no child to end and the client door works normally.
    /// </summary>
    internal OpenCodeServer(Uri endpoint, string password)
    {
        _endpoint = endpoint;
        _password = password;
    }

    /// <summary>
    /// Initializes a handle over a server another process runs: the identity a registration
    /// published, no child, no ownership. Disposal is a no-op.
    /// </summary>
    internal OpenCodeServer(Uri endpoint, string password, int processId)
    {
        _endpoint = endpoint;
        _password = password;
        _processId = processId;
    }

    /// <summary>
    /// Initializes a mocking instance; members invoked without an override throw an instructive failure.
    /// </summary>
    protected OpenCodeServer()
    {
    }

    /// <summary>
    /// Gets a value indicating whether this handle owns the server process: true for a server
    /// <see cref="StartAsync"/> started, whose disposal ends it; false for a registered background
    /// service <see cref="DiscoverAsync"/> or <see cref="EnsureAsync"/> found, whose disposal is a
    /// no-op because other clients share it. A bare mock reports false unless it overrides this
    /// member.
    /// </summary>
    public virtual bool OwnsProcess => _child is not null;

    /// <summary>
    /// Gets a task that completes once the owned child's output readers hold no thread any more;
    /// friend-assembly test seam for the release disposal guarantees.
    /// </summary>
    internal Task OutputReadersEnded => _child?.ReadersEnded ?? Task.CompletedTask;

    /// <summary>Gets the endpoint: the port-zero binding of a started server, or the URL a registration published.</summary>
    public virtual Uri Endpoint => _endpoint ?? throw MockSeam.CreateError("OpenCodeServer", "Endpoint");

    /// <summary>Gets the basic-authentication username; the pinned server accepts only <c>opencode</c>.</summary>
    public virtual string Username => "opencode";

    /// <summary>Gets the credential: the generated lease a start injected into its child, or the password a registration published.</summary>
    public virtual string Password => _password ?? throw MockSeam.CreateError("OpenCodeServer", "Password");

    /// <summary>
    /// Gets the server's process identifier, for diagnostics and process-truth assertions. It
    /// remains readable after disposal, when any process handle is gone. For a started server it
    /// identifies the process this door owns, which for a Windows batch shim is the cmd.exe host
    /// the shim's own child runs under rather than the server process (and which the job that ends
    /// the server with its owner holds); ask the server for its own pid when that distinction
    /// matters. For a discovered service it is the pid the registration
    /// published and the info answer confirmed.
    /// </summary>
    public virtual int ProcessId =>
        _processId ?? throw MockSeam.CreateError("OpenCodeServer", "ProcessId");

    /// <summary>
    /// Builds a client for this server. The door owns the connection identity: the delegate
    /// receives identity-unset options, and setting Endpoint, Username, or Password there is
    /// refused. Behavior members such as <see cref="OpenCodeClientOptions.Location"/> configure
    /// freely. Every call builds a new client owning its transport; the caller disposes it.
    /// </summary>
    /// <param name="configure">Optional behavior configuration; identity members must stay unset.</param>
    /// <returns>A new client bound to this server's endpoint and lease credential.</returns>
    /// <exception cref="InvalidOperationException">The delegate set an identity member.</exception>
    public virtual OpenCodeClient CreateClient(Action<OpenCodeClientOptions>? configure = null)
    {
        var endpoint = Endpoint;
        var password = Password;
        var options = new OpenCodeClientOptions();
        configure?.Invoke(options);
        if (options.Endpoint is not null ||
            options.Password is not null ||
            !string.Equals(options.Username, "opencode", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CreateClient owns the connection identity: leave Endpoint, Username, and Password unset — the started server supplies them. Configure behavior members such as Location only.");
        }

        // A distinct instance carries the door's identity into the client: the object the
        // delegate received stays identity-unset for as long as the caller keeps a reference to
        // it, rather than being mutated out from under them after the delegate returns.
        return new OpenCodeClient(new OpenCodeClientOptions
        {
            Endpoint = endpoint,
            Username = options.Username,
            Password = password,
            Location = options.Location,
            NetworkTimeout = options.NetworkTimeout,
        });
    }

    /// <summary>
    /// Ends the owned child, bounded at every step, quiet for a child that is already gone, and
    /// idempotent. On Windows, as upstream ends its server there: ends the server and every live
    /// descendant of it at once with <c>taskkill /T /F</c>, bounded by the configured grace, which
    /// starts with it; <c>TerminateProcess</c> ends the server with code 1 when the kill fails; when
    /// the server is still running at the grace's end, the same pair runs again; stdin closes last.
    /// A server that already exited is not tree-killed. On Linux and macOS: sends <c>SIGTERM</c> to the
    /// server's process group, waits the configured grace for the server and then for the rest of
    /// its group to end, sends <c>SIGKILL</c> to the group when anything of it is left, and closes
    /// stdin last. A child that moved into a session of its own is left to the server, and a server
    /// that already exited on its own with code 0 or on a signal leaves its group alone. An
    /// <see cref="OpenCodeServerOptions.Output"/> collector keeps collecting while the server is
    /// ended, and its output is drained within a bound of at most one second before the collection
    /// closes. A handle that owns no process (<see cref="OwnsProcess"/>
    /// false) has nothing to end: disposing a discovered service never stops it.
    /// </summary>
    /// <returns>A task that completes once any owned child is ended and released.</returns>
    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 1)
        {
            return;
        }

        GC.SuppressFinalize(this);
        if (_child is null)
        {
            return;
        }

        try
        {
            await _child.EndAsync().ConfigureAwait(false);
        }
        finally
        {
            await _child.DisposeAsync().ConfigureAwait(false);
        }
    }
}
