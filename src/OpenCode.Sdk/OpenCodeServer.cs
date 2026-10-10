using System.Globalization;
using System.Security.Cryptography;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.BackgroundService;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Contender;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;
using OpenCode.Sdk.Internal.BackgroundService.Handoff;
using OpenCode.Sdk.Internal.BackgroundService.ProcessControl;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.BackgroundService.Stop;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;

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
public class OpenCodeServer : IAsyncDisposable
{
    private const int StderrRetainedLines = 40;

    /// <summary>The reference client's exact standalone argv tail, appended to every command.</summary>
    private static readonly string[] LauncherArguments = ["--stdio", "--port", "0"];

    private readonly OwnedServerChild? _child;
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

    /// <summary>
    /// Gets the owned child's own exit, as the launcher observed it; friend-assembly test seam for
    /// how the disposal ladder ended the server.
    /// </summary>
    internal Task<ChildExitStatus> ChildExited =>
        _child?.Exited ?? throw new InvalidOperationException("This handle owns no process.");

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
    /// Finds the registered background service the first-party CLI publishes for this user,
    /// without starting, owning, or stopping it: resolves the registration file from the channel
    /// and the XDG roots (or reads the file the options name directly), runs the channel's legacy
    /// migration, decodes the registration, and asks the daemon for its info under a two-second
    /// bound. A ready daemon that carries a password and, when <see cref="OpenCodeServerDiscoverOptions.ExpectedVersion"/>
    /// is set, reports exactly that version, comes back as a non-owning handle; anything else is null.
    /// </summary>
    /// <param name="options">The discovery options; null reads the shared release registration.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates, the internal request bound does not.</param>
    /// <returns>A non-owning handle over the ready service, or null when there is no usable one.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves for an XDG fallback.</exception>
    public static Task<OpenCodeServer?> DiscoverAsync(
        OpenCodeServerDiscoverOptions? options = null,
        CancellationToken cancellationToken = default) =>
        DiscoverWithSeamsAsync(options, PlatformProbe(ServiceTiming.Default), cancellationToken);

    /// <summary>
    /// <see cref="DiscoverAsync"/> over an injected probe, the seam the isolated test executable uses
    /// to report the probe's own verdict: discovery answers null alike for no service and for a
    /// probe that timed out, and only the verdict tells the two apart.
    /// </summary>
    /// <param name="options">The discovery options; null reads the shared release registration.</param>
    /// <param name="probe">The info probe; the public door passes the platform probe at the pinned timing.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>A non-owning handle over the ready service, or null when there is no usable one.</returns>
    internal static async Task<OpenCodeServer?> DiscoverWithSeamsAsync(
        OpenCodeServerDiscoverOptions? options,
        IServiceInfoProbe probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var discovery = new ServiceDiscovery(new ServiceEnvironment(), new ServiceFileSystem(), probe);
        var registration = await discovery.DiscoverAsync(options, cancellationToken).ConfigureAwait(false);
        return registration is null ? null : SharedService(registration);
    }

    /// <summary>The info probe over the platform's loopback transport and socket connect.</summary>
    private static ServiceInfoProbe PlatformProbe(ServiceTiming timing) =>
        new(timing, new LoopbackTransport(new LoopbackSocketConnector()));

    /// <summary>
    /// The non-owning handle over a registered service. Discover and the Ensure election return only
    /// registrations that carry a password, the handle's non-null credential.
    /// </summary>
    private static OpenCodeServer SharedService(ServiceRegistration registration) =>
        new(
            registration.Endpoint,
            registration.Password ?? throw new InvalidOperationException(
                "A registered service reached the handle without a password; Discover and Ensure never return one."),
            registration.ProcessId);

    /// <summary>
    /// Stops the registered background service the way <c>opencode service stop</c> does: resolves
    /// the registration the options name (channel rules and legacy migration included), asks a
    /// ready and compatible daemon to shut its persistent terminals down, clears the handoff
    /// sidecar as far as it can, then ends the registered process — a request to stop first, a
    /// hard kill when it survives — and removes the registration once the process is gone. The
    /// registration is re-read before the first signal and before the removal, so a service that
    /// re-registered before the stop began is never signalled and a successor's registration is
    /// never removed; the hard kill follows the signalled process alone. Every signal compares the
    /// process's identity (pid and start time), so a pid the operating system reused is never
    /// signalled. A missing or corrupt registration completes successfully; the shared service is
    /// shared, and this call is the one deliberate way to end it — disposing a discovered handle
    /// never does.
    /// </summary>
    /// <param name="options">The stop options; null stops the shared release registration.</param>
    /// <param name="cancellationToken">The caller's token. Cancellation before the first signal prevents it; after a signal it ends the bounded wait and leaves the registration in place.</param>
    /// <returns>A task that completes when the registered process is gone or there was none to stop.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves for an XDG fallback, or the registered process is still running after the hard kill.</exception>
    public static Task StopAsync(
        OpenCodeServerStopOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var timing = ServiceTiming.Default;
        var fileSystem = new ServiceFileSystem();
        var ptyShutdown = new ServicePtyShutdown();
        var stopper = new ServiceStopper(
            new ServiceEnvironment(),
            fileSystem,
            PlatformProbe(timing),
            ptyShutdown,
            new ServicePtyHandoff(fileSystem, new ServiceClock(), ptyShutdown),
            new ServiceProcessControl(),
            timing);
        return stopper.StopAsync(options, cancellationToken);
    }

    /// <summary>
    /// Ensures a healthy compatible background service is running: reuses a ready daemon,
    /// replaces a version-mismatched one according to
    /// <see cref="OpenCodeServerEnsureOptions.VersionPolicy"/>, and otherwise spawns detached
    /// contenders until one registers or the wall-clock bound expires. The returned handle does
    /// not own the process; disposal is a no-op, and <see cref="StopAsync"/> is the one
    /// deliberate way to end the shared service.
    /// </summary>
    /// <param name="options">The ensure options; null uses every default, including
    /// <c>opencode serve --service</c> and <see cref="OpenCodeServerVersionPolicy.Ignore"/>.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>A non-owning handle over the ready service.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, the command is empty, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves, the election timed out, a contender or the registered service failed to start, the registered service speaks an incompatible health protocol at a version the call accepts, a version mismatch was refused, or a spawned command could not be resolved.</exception>
    public static Task<OpenCodeServer> EnsureAsync(
        OpenCodeServerEnsureOptions? options = null,
        CancellationToken cancellationToken = default) =>
        EnsureWithSeamsAsync(options, ServiceTiming.Default, new ServiceContenderSpawner(new PosixSpawn(), new WindowsSpawn()), cancellationToken);

    /// <summary>
    /// The seam-injected Ensure the tests use, kept out of the public option types the way upstream's
    /// <c>withEnsureTiming</c> keeps test timing out of its own: an internal seam, never on
    /// <see cref="OpenCodeServerEnsureOptions"/>, so the shipped surface stays unchanged. A live
    /// proof accelerates the election loop's spawn delay and probe bound through the timing, and
    /// wraps the contender spawner to learn every process the election starts — the door releases
    /// its contenders rather than returning them, the way the pinned loop does, so nothing else can.
    /// </summary>
    /// <param name="options">The ensure options; null uses every default.</param>
    /// <param name="timing">The lifecycle timing; the public door passes <see cref="ServiceTiming.Default"/>.</param>
    /// <param name="spawner">The contender spawn; the public door passes the platform spawner.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>A non-owning handle over the ready service.</returns>
    internal static async Task<OpenCodeServer> EnsureWithSeamsAsync(
        OpenCodeServerEnsureOptions? options,
        ServiceTiming timing,
        IServiceContenderSpawner spawner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(spawner);

        var fileSystem = new ServiceFileSystem();
        var clock = new ServiceClock();
        var ensurer = new ServiceEnsurer(
            new ServiceEnvironment(),
            fileSystem,
            PlatformProbe(timing),
            spawner,
            new ServicePtyHandoff(fileSystem, clock, new ServicePtyShutdown()),
            new ServiceProcessControl(),
            clock,
            new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()),
            timing);
        var registration = await ensurer.EnsureAsync(options, cancellationToken).ConfigureAwait(false);
        return SharedService(registration);
    }

    /// <summary>
    /// Starts a fresh private standalone server: resolves the command the way a shell would,
    /// spawns it with <c>--stdio --port 0</c> appended and the generated lease credential in the
    /// child environment, then waits for the JSON readiness line. On any failure that reached a
    /// child, the child is ended before the method throws. On Windows the server is created
    /// suspended, placed in the process-wide job that ends it with its owner, and only then
    /// resumed; a failed start ends it the way disposal does, with <c>taskkill /T /F</c> at once and
    /// again after <see cref="OpenCodeServerOptions.GracefulShutdownTimeout"/> when it is still
    /// running, so a canceled or timed-out start can take up to that grace plus 26 seconds to throw.
    /// On Linux and macOS the server is spawned in a session of its own, and a failed start ends it
    /// the way disposal does: <c>SIGTERM</c> to its process group, the grace, then <c>SIGKILL</c>. A
    /// canceled or timed-out start can therefore take up to that grace plus 16 seconds to throw: at
    /// most 10 for the server's exit after <c>SIGKILL</c>, 1 for draining its output, and 5 for
    /// releasing the output readers.
    /// </summary>
    /// <param name="options">The launch options; null uses the defaults.</param>
    /// <param name="cancellationToken">The cancellation token ending the wait for readiness.</param>
    /// <returns>The started server, disposed by the caller.</returns>
    /// <exception cref="ArgumentException">The options are unusable: an empty command, a blank command entry, a non-positive readiness timeout, a negative grace, an output collector an earlier start already bound, or a NUL in a command entry or an environment entry.</exception>
    /// <exception cref="OpenCodeServerException">The command did not resolve on PATH, a leading argument was refused for a Windows batch shim, the platform is neither Windows, Linux, nor macOS, or the process could not start, could not be placed in the job that ends it with its owner (Windows), exited or closed its stdout before readiness, timed out, or broke the readiness contract.</exception>
    public static Task<OpenCodeServer> StartAsync(
        OpenCodeServerOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StartWithSeamsAsync(options, LauncherSeams.ForCurrentProcess(), cancellationToken);

    /// <summary>
    /// The seam-injected start the tests use, the way <see cref="EnsureWithSeamsAsync"/> keeps its
    /// seams off the public options: a live proof substitutes the Windows tree kill, a POSIX seam,
    /// or the host environment. The public door passes the platform's own.
    /// </summary>
    /// <param name="options">The launch options; null uses the defaults.</param>
    /// <param name="seams">The seams for this start and the server's disposal.</param>
    /// <param name="cancellationToken">The cancellation token ending the wait for readiness.</param>
    /// <returns>The started server, disposed by the caller.</returns>
    internal static async Task<OpenCodeServer> StartWithSeamsAsync(
        OpenCodeServerOptions? options,
        LauncherSeams seams,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seams);

        options ??= new OpenCodeServerOptions();
        var command = SnapshotCommand(options);
        ValidateTimeouts(options);
        var readinessTimeout = options.ReadinessTimeout;
        var output = BindOutput(options);

        var password = GeneratePassword();

        // Ownership stays local until the very end: every failure path throws through this
        // try, and the finally is the single place that releases the child on that path. The
        // flag is set only once the new OpenCodeServer has taken ownership on success
        // (TransportPolicy.CreateOwnedHttpClient's handler-ownership idiom, mirrored here).
        OwnedServerChild? child = null;
        var started = false;
        try
        {
            // Once per start, before anything is spawned: what the process starts, and what a
            // failure names, is the resolved target rather than the bare name the caller wrote.
            var executable = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess())
                .Resolve(command[0]);
            var stderrGate = new object();
            var stderrTail = new Queue<string>(StderrRetainedLines);
            var readyLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (onStandardOutput, onStandardError) = CreateOutputHandlers(readyLine, stderrGate, stderrTail, output);
            child = await ServerChild.LaunchAsync(
                new ServerChildStart
                {
                    Executable = executable,
                    SuppliedArguments = [.. command.Skip(1)],
                    LauncherArguments = LauncherArguments,
                    Environment = options.Environment,
                    Password = password,
                    WorkingDirectory = options.WorkingDirectory,
                    GracefulShutdownTimeout = options.GracefulShutdownTimeout,
                    Output = output,
                    OnStandardOutput = onStandardOutput,
                    OnStandardError = onStandardError,
                },
                seams).ConfigureAwait(false);

            var line = await WaitForReadyLineAsync(
                child, readyLine, readinessTimeout, stderrGate, stderrTail, cancellationToken).ConfigureAwait(false);
            if (!ServerReadyLine.TryParse(line, out var endpoint))
            {
                await child.EndFailedStartAsync().ConfigureAwait(false);
                throw new OpenCodeServerException(
                    $"The server's first stdout line is not the JSON readiness contract: '{line}'.{DescribeStderr(stderrGate, stderrTail)}");
            }

            var server = new OpenCodeServer(child, endpoint, password);
            started = true;
            return server;
        }
        finally
        {
            if (!started)
            {
                // A failed start: the child was ended and its output drained (as far as the
                // bound allowed) on the way here. When an unexpected failure skipped that, the
                // POSIX release ends the child first. Releasing the readers ends any read still
                // waiting, so no reader outlives the start and what the collector holds is final.
                if (child is not null)
                {
                    await child.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    output?.Complete();
                }
            }
        }
    }

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

    private static OpenCodeServerOutput? BindOutput(OpenCodeServerOptions options)
    {
        var output = options.Output;
        if (output is not null && !output.TryBind())
        {
            throw new ArgumentException(
                "OpenCodeServerOptions.Output is already bound to an earlier start; create a new OpenCodeServerOutput per start.",
                nameof(options));
        }

        return output;
    }

    private static void ValidateTimeouts(OpenCodeServerOptions options)
    {
        if (options.ReadinessTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("OpenCodeServerOptions.ReadinessTimeout must be positive.", nameof(options));
        }

        if (options.GracefulShutdownTimeout < TimeSpan.Zero)
        {
            throw new ArgumentException("OpenCodeServerOptions.GracefulShutdownTimeout cannot be negative.", nameof(options));
        }
    }

    private static (Action<string> StandardOutput, Action<string> StandardError) CreateOutputHandlers(
        TaskCompletionSource<string> readyLine,
        object stderrGate,
        Queue<string> stderrTail,
        OpenCodeServerOutput? output)
    {
        // Continuous drain: the first stdout line is the readiness contract; every later stdout
        // write is read and, unless a collector retains it, dropped so a chatty server can never
        // fill the pipe and wedge the probe (the reference keeps draining too, in standalone.ts).
        // The collector's append takes only its own bounded-data lock.
        void OnStandardOutput(string line)
        {
            // Retain first, then signal: the readiness continuation can run the moment the
            // result is set, and a snapshot taken right after StartAsync returns must already
            // hold the line that made it return.
            output?.AppendStandardOutput(line);
            _ = readyLine.TrySetResult(line);
        }

        void OnStandardError(string line)
        {
            lock (stderrGate)
            {
                stderrTail.Enqueue(line);
                if (stderrTail.Count > StderrRetainedLines)
                {
                    _ = stderrTail.Dequeue();
                }
            }

            output?.AppendStandardError(line);
        }

        return (OnStandardOutput, OnStandardError);
    }

    private static async Task<string> WaitForReadyLineAsync(
        OwnedServerChild child,
        TaskCompletionSource<string> readyLine,
        TimeSpan readinessTimeout,
        object stderrGate,
        Queue<string> stderrTail,
        CancellationToken cancellationToken)
    {
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(readinessTimeout);
        ReadinessOutcome outcome;
        try
        {
            outcome = await child.WaitForReadinessAsync(readyLine.Task, readiness.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await child.EndFailedStartAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The server start was canceled.", exception, cancellationToken);
            }

            throw new OpenCodeServerException(
                $"The server did not report readiness within {readinessTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.{DescribeStderr(stderrGate, stderrTail)}");
        }

        if (outcome.ReadyLine is { } line)
        {
            return line;
        }

        var ending = outcome.Exit is { } exit ? exit.Describe() : "closed its standard output";
        throw new OpenCodeServerException(
            $"The server {ending} before reporting readiness.{DescribeStderr(stderrGate, stderrTail)}");
    }

    private static string[] SnapshotCommand(OpenCodeServerOptions options)
    {
        var command = options.Command;
        if (command is not { Count: > 0 })
        {
            throw new ArgumentException(
                "OpenCodeServerOptions.Command needs the executable and its leading arguments.", nameof(options));
        }

        var snapshot = new string[command.Count];
        for (var index = 0; index < command.Count; index++)
        {
            var entry = command[index];
            if (string.IsNullOrWhiteSpace(entry))
            {
                throw new ArgumentException("OpenCodeServerOptions.Command entries cannot be blank.", nameof(options));
            }

            snapshot[index] = entry;
        }

        return snapshot;
    }

    private static string GeneratePassword()
    {
        var bytes = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);

        // base64url, mirroring the reference client's randomBytes(32).toString("base64url").
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string DescribeStderr(object gate, Queue<string> tail)
    {
        lock (gate)
        {
            return tail.Count == 0 ? string.Empty : string.Concat(" Recent stderr: ", string.Join(" | ", tail));
        }
    }
}
