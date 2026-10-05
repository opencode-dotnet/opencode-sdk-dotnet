using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk;

/// <summary>
/// A local opencode server reached through one of its process-backed modes, with the ownership
/// the mode implies visible through <see cref="OwnsProcess"/>. <see cref="StartAsync"/> returns
/// an owned standalone server: started on port zero with its own generated lease credential, held
/// through an open stdin pipe, and ended by disposal — stdin EOF first, then a bounded grace, then
/// a forced tree kill; disposal ends exactly its own child, and the operating system closes the
/// lease even when the owner crashes before disposal runs. <see cref="DiscoverAsync"/> returns a
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

    private static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounds every wait for the child's redirected output to reach end-of-stream. End-of-stream
    /// arrives only when every process holding a write end closes it, and a surviving descendant
    /// can hold one open, so a caller always regains control within this window regardless of
    /// what the child (or anything the child spawned) is still holding open.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Process? _process;
    private readonly ChildOutputPump? _pump;
    private readonly ProcessTreeTerminator? _terminator;
    private readonly Uri? _endpoint;
    private readonly string? _password;
    private readonly TimeSpan _gracefulShutdownTimeout;
    private readonly OpenCodeServerOutput? _output;
    private readonly int? _processId;
    private int _disposed;

    private OpenCodeServer(
        Process process,
        ChildOutputPump pump,
        ProcessTreeTerminator terminator,
        Uri endpoint,
        string password,
        TimeSpan gracefulShutdownTimeout,
        OpenCodeServerOutput? output)
    {
        _process = process;
        _pump = pump;
        _terminator = terminator;
        _endpoint = endpoint;
        _password = password;
        _gracefulShutdownTimeout = gracefulShutdownTimeout;
        _output = output;

        // Captured while the handle is live: the identity stays readable for diagnostics after
        // disposal has released the process handle.
        _processId = process.Id;
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
    public virtual bool OwnsProcess => _process is not null;

    /// <summary>
    /// Gets a task that completes once the owned child's output readers hold no thread any more;
    /// friend-assembly test seam for the release disposal guarantees.
    /// </summary>
    internal Task OutputReadersEnded => _pump?.ReadersEnded ?? Task.CompletedTask;

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
    /// the shim's own child runs under rather than the server process; ask the server for its own
    /// pid when that distinction matters. For a discovered service it is the pid the registration
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
        DiscoverWithSeamsAsync(options, new ServiceInfoProbe(ServiceTiming.Default), cancellationToken);

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
    /// sidecar, then ends the registered process — a request to stop first, a hard kill when it
    /// survives — and removes the registration once the process is gone. Every step re-reads the
    /// registration and compares the process's identity (pid and start time), so a service that
    /// re-registered or a pid the operating system reused is never signalled. A missing or corrupt
    /// registration completes successfully; the shared service is shared, and this call is the one
    /// deliberate way to end it — disposing a discovered handle never does.
    /// </summary>
    /// <param name="options">The stop options; null stops the shared release registration.</param>
    /// <param name="cancellationToken">The caller's token. Cancellation before the first signal prevents it; after a signal it ends the bounded wait and leaves the registration in place.</param>
    /// <returns>A task that completes when the registered process is gone or there was none to stop.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves for an XDG fallback, the handoff sidecar could not be removed, or the registered process is still running after the hard kill.</exception>
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
            new ServiceInfoProbe(timing),
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
    /// <exception cref="OpenCodeServerException">No user home directory resolves, the election timed out, the service failed to start, a version mismatch was refused, or a spawned command could not be resolved.</exception>
    public static Task<OpenCodeServer> EnsureAsync(
        OpenCodeServerEnsureOptions? options = null,
        CancellationToken cancellationToken = default) =>
        EnsureWithSeamsAsync(options, ServiceTiming.Default, new ServiceContenderSpawner(), cancellationToken);

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
            new ServiceInfoProbe(timing),
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
    /// child, the tree is ended before the method throws.
    /// </summary>
    /// <param name="options">The launch options; null uses the defaults.</param>
    /// <param name="cancellationToken">The cancellation token ending the wait for readiness.</param>
    /// <returns>The started server, disposed by the caller.</returns>
    /// <exception cref="ArgumentException">The options are unusable: an empty command, a blank command entry, a non-positive readiness timeout, a negative grace, or an output collector an earlier start already bound.</exception>
    /// <exception cref="OpenCodeServerException">The command did not resolve on PATH, a leading argument was refused for a Windows batch shim, or the process could not start, exited before readiness, timed out, or broke the readiness contract.</exception>
    public static Task<OpenCodeServer> StartAsync(
        OpenCodeServerOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StartWithSeamsAsync(options, ProcessTreeTerminator.Platform, cancellationToken);

    /// <summary>
    /// The seam-injected start the tests use, the way <see cref="EnsureWithSeamsAsync"/> keeps its
    /// seams off the public options: a live proof substitutes the whole-tree kill to script a kill
    /// the platform reports incomplete. The public door passes the platform terminator.
    /// </summary>
    /// <param name="options">The launch options; null uses the defaults.</param>
    /// <param name="terminator">The forced rung's tree kill, for this start and the server's disposal.</param>
    /// <param name="cancellationToken">The cancellation token ending the wait for readiness.</param>
    /// <returns>The started server, disposed by the caller.</returns>
    internal static async Task<OpenCodeServer> StartWithSeamsAsync(
        OpenCodeServerOptions? options,
        ProcessTreeTerminator terminator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminator);

        options ??= new OpenCodeServerOptions();
        var command = SnapshotCommand(options);
        ValidateTimeouts(options);
        var readinessTimeout = options.ReadinessTimeout;
        var gracefulShutdownTimeout = options.GracefulShutdownTimeout;
        var output = BindOutput(options);

        var password = GeneratePassword();

        // Ownership stays local until the very end: every failure path throws through this
        // try, and the finally is the single place that disposes the child on that path. The
        // local is nulled only once the new OpenCodeServer has taken ownership on success
        // (TransportPolicy.CreateOwnedHttpClient's handler-ownership idiom, mirrored here).
        Process? process = null;
        ChildOutputPump? pump = null;
        try
        {
            // Once per start, before anything is spawned: what the process starts, and what a
            // failure names, is the resolved target rather than the bare name the caller wrote.
            var executable = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess())
                .Resolve(command[0]);
            process = CreateProcess(executable, command, options, password);
            var stderrGate = new object();
            var stderrTail = new Queue<string>(StderrRetainedLines);
            var readyLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (onStandardOutput, onStandardError) = CreateOutputHandlers(readyLine, stderrGate, stderrTail, output);
            pump = StartChildProcess(process, executable, onStandardOutput, onStandardError);

            var line = await WaitForReadyLineAsync(
                process, pump, terminator, readyLine, readinessTimeout, stderrGate, stderrTail, cancellationToken).ConfigureAwait(false);
            if (!ServerReadyLine.TryParse(line, out var endpoint))
            {
                _ = await EndStartupFailureAsync(process, pump, terminator).ConfigureAwait(false);
                throw new OpenCodeServerException(
                    $"The server's first stdout line is not the JSON readiness contract: '{line}'.{DescribeStderr(stderrGate, stderrTail)}");
            }

            var started = new OpenCodeServer(process, pump, terminator, endpoint, password, gracefulShutdownTimeout, output);
            process = null;
            pump = null;
            return started;
        }
        finally
        {
            if (process is not null)
            {
                // A failed start: the child was ended and its output drained (as far as the
                // bound allowed) on the way here. Releasing the readers ends any read still
                // waiting, so no reader outlives the start and what the collector holds is final.
                if (pump is not null)
                {
                    await pump.ReleaseAsync().ConfigureAwait(false);
                }

                output?.Complete();
            }

            process?.Dispose();
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
    /// Ends the owned child: closes stdin (the ownership lease), waits the configured grace,
    /// then escalates to a forced tree kill. Idempotent, bounded, and quiet for a child that is
    /// already gone. A handle that owns no process (<see cref="OwnsProcess"/> false) has nothing
    /// to end: disposing a discovered service never stops it.
    /// </summary>
    /// <returns>A task that completes once any owned child is ended and released.</returns>
    public virtual async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 1)
        {
            return;
        }

        GC.SuppressFinalize(this);
        if (_process is null || _pump is null || _terminator is null)
        {
            return;
        }

        try
        {
            await EndOwnedChildAsync(_process, _gracefulShutdownTimeout, _terminator).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_output is not null)
                {
                    // The collector's promise is a final snapshot once disposal returns. The
                    // bounded drain lets the redirected readers reach end-of-stream (or its bound)
                    // before the collection closes; the child was ended above, and a drain that
                    // cannot finish inside its bound leaves an honest, possibly incomplete, tail.
                    _ = await _pump.DrainAsync(DrainTimeout).ConfigureAwait(false);
                }

                // Every reader ends before the process is released, collector or not: a read
                // still waiting for end-of-stream (a descendant holding the pipe) is canceled here.
                await _pump.ReleaseAsync().ConfigureAwait(false);
                _output?.Complete();
            }
            finally
            {
                _process.Dispose();
            }
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

    private static ChildOutputPump StartChildProcess(
        Process process,
        ResolvedExecutable executable,
        Action<string> onStandardOutput,
        Action<string> onStandardError)
    {
        try
        {
            _ = process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new OpenCodeServerException(
                $"Failed to start the server command '{executable.Command}'{DescribeResolution(executable)}.",
                exception);
        }

        return ChildOutputPump.Start(process, onStandardOutput, onStandardError);
    }

    private static async Task<string> WaitForReadyLineAsync(
        Process process,
        ChildOutputPump pump,
        ProcessTreeTerminator terminator,
        TaskCompletionSource<string> readyLine,
        TimeSpan readinessTimeout,
        object stderrGate,
        Queue<string> stderrTail,
        CancellationToken cancellationToken)
    {
        var exit = process.WaitForExitAsync(CancellationToken.None);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(readinessTimeout);
        try
        {
            _ = await Task.WhenAny(readyLine.Task, exit).WaitAsync(readiness.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            _ = await EndStartupFailureAsync(process, pump, terminator).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The server start was canceled.", exception, cancellationToken);
            }

            throw new OpenCodeServerException(
                $"The server did not report readiness within {readinessTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.{DescribeStderr(stderrGate, stderrTail)}");
        }

        if (readyLine.Task.IsCompleted)
        {
            return await readyLine.Task.ConfigureAwait(false);
        }

        var exitCode = process.ExitCode;

        // The root already exited on its own, but a launcher shim (a .cmd/bun wrapper that
        // spawns the real server and exits) can leave live grandchildren holding the redirected
        // pipe handles open — which would make the drain below wait for an EOF that never comes.
        // Killing the tree first closes that gap before the bounded drain runs; whether there was
        // still a tree to end, and whether the drain reached EOF inside its bound, change nothing
        // about what this failure reports.
        _ = terminator.TryKill(process);
        _ = await pump.DrainAsync(DrainTimeout).ConfigureAwait(false);
        throw new OpenCodeServerException(
            $"The server exited with code {exitCode.ToString(CultureInfo.InvariantCulture)} before reporting readiness.{DescribeStderr(stderrGate, stderrTail)}");
    }

    private static async Task EndOwnedChildAsync(Process process, TimeSpan grace, ProcessTreeTerminator terminator)
    {
        if (ProcessRootExit.HasExited(process))
        {
            return;
        }

        // Stdin EOF ends the scoped server lifetime (server-process.ts); closing the
        // redirected writer is the lease release. A lease that was already gone means the child
        // is already leaving, so the bounded wait below covers both outcomes.
        ReleaseStdinLease(process);

        // The child's own exit, not its output's end-of-stream: a descendant still holding the
        // pipe must not hold the grace open. The drain has its own bound.
        if (await ProcessRootExit.WaitWithinAsync(process, grace).ConfigureAwait(false))
        {
            return;
        }

        // An incomplete kill (some process of the tree refused it) still ended what it could
        // reach; the ladder continues the same way, and every release step after it runs.
        _ = terminator.TryKill(process);

        // Bounded on purpose: the kill was issued, and a disposal never hangs the caller, so a
        // child the operating system has not reaped inside this window is left to it.
        _ = await ProcessRootExit.WaitWithinAsync(process, ForcedExitTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the ownership lease by closing the redirected stdin writer; a pipe that is already
    /// gone reports the child leaving rather than a failure to end it.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "Closing the lease is the release itself: a writer that went with the process, or a broken pipe, reports that the child is already leaving, which is what the release asks for.")]
    private static void ReleaseStdinLease(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // The lease is released either way: the child is already leaving.
        }
    }

    /// <summary>
    /// Ends a child that failed to reach readiness and drains its redirected output, so the stderr
    /// tail the caller is about to quote is as complete as the bound allows.
    /// </summary>
    /// <returns>
    /// True when the drain ran to completion; false when it could not run at all. Either way the
    /// startup failure reaches the caller as its own exception rather than as a teardown fault,
    /// which is why every call site discards this: an incomplete tail is still the best evidence
    /// available, and there is no second attempt worth making on a process being abandoned.
    /// </returns>
    private static async Task<bool> EndStartupFailureAsync(Process process, ChildOutputPump pump, ProcessTreeTerminator terminator)
    {
        _ = terminator.TryKill(process);
        try
        {
            if (!await ProcessRootExit.WaitWithinAsync(process, ForcedExitTimeout).ConfigureAwait(false))
            {
                // The bounded exit wait expired without the process ending; skip the drain rather
                // than wait on a process that may still be alive and writing.
                return false;
            }

            return await pump.DrainAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No process is associated with the object any more: there is nothing left to wait
            // for, and nothing left holding the redirected pipes open either.
            return false;
        }
        catch (Win32Exception)
        {
            // The handle is gone or inaccessible; the outer disposal releases what remains.
            return false;
        }
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

    private static Process CreateProcess(
        ResolvedExecutable executable, string[] command, OpenCodeServerOptions options, string password)
    {
        var process = new Process();
        var startInfo = process.StartInfo;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        if (options.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        ConfigureCommandLine(startInfo, executable, [.. command.Skip(1)]);
        if (options.Environment is not null)
        {
            foreach (var entry in options.Environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        // The explicit entry wins over anything inherited or supplied: the child's lease
        // credential is always this start's own (upstream standalone.ts command's posture; stdio
        // mode scrubs it from the env the server hands to tools, server-process.ts).
        startInfo.Environment["OPENCODE_PASSWORD"] = password;
        process.EnableRaisingEvents = true;
        return process;
    }

    /// <summary>
    /// Points the start info at what actually runs and composes its command line. A batch shim
    /// never becomes the FileName: cmd.exe does, with the script as its first quoted token, so the
    /// launch is one documented parse instead of CreateProcess's implicit batch handling. The
    /// consequences are real and deliberate — the redirected stdin lease and the stdout readiness
    /// line pass through the interpreter to the child, and the owned root this launcher reports as
    /// <see cref="ProcessId"/> is that interpreter, whose descendants the bounded tree kill covers.
    /// </summary>
    private static void ConfigureCommandLine(
        ProcessStartInfo startInfo, ResolvedExecutable executable, IReadOnlyList<string> suppliedArguments)
    {
        if (executable.IsBatchScript)
        {
            startInfo.FileName = BatchCommandLine.InterpreterPath;

            // One composed string on every target: cmd.exe does not follow the MSVCRT rules
            // ArgumentList applies, so the batch case never routes through that door.
            startInfo.Arguments = BatchCommandLine.Compose(
                executable.Path, suppliedArguments, LauncherArguments);
            return;
        }

        startInfo.FileName = executable.Path;
        var arguments = suppliedArguments.Concat(LauncherArguments);
#if NET
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
#else
        // ArgumentList does not exist downlevel; the composed string follows the MSVCRT rules.
        startInfo.Arguments = ProcessArgumentComposer.Compose(arguments);
#endif
    }

    /// <summary>Names the resolved target alongside the caller's spelling, when they differ.</summary>
    private static string DescribeResolution(ResolvedExecutable executable) =>
        string.Equals(executable.Command, executable.Path, StringComparison.Ordinal)
            ? string.Empty
            : $" (resolved to '{executable.Path}')";

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
