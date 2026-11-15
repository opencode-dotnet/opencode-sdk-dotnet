using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

public partial class OpenCodeServer
{
    /// <summary>
    /// Finds the registered background service the first-party CLI publishes for this user,
    /// without starting, owning, or stopping it: resolves the registration file from the channel
    /// and the XDG roots (or reads the file the options name directly), runs the channel's legacy
    /// migration, decodes the registration, and asks the daemon for its info under a two-second
    /// bound. A ready daemon that, when <see cref="OpenCodeServerDiscoverOptions.ExpectedVersion"/>
    /// is set, reports exactly that version, comes back as a non-owning handle; anything else is null.
    /// </summary>
    /// <param name="options">The discovery options; null reads the shared release registration.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates, the internal request bound does not.</param>
    /// <returns>A non-owning handle over the ready service, or null when there is no usable one.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves for an XDG fallback.</exception>
    public static Task<OpenCodeServer?> DiscoverAsync(
        OpenCodeServerDiscoverOptions? options = null,
        CancellationToken cancellationToken = default)
        => DiscoverWithSeamsAsync(options, SystemDiscoveryProbe(), cancellationToken);

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

    /// <summary>The shared discovery composition root's system-clock probe.</summary>
    private static ServiceInfoProbe SystemDiscoveryProbe()
    {
#pragma warning disable RS0030 // The discovery composition root chooses the system clock and passes it to its info probe.
        var time = TimeProvider.System;
#pragma warning restore RS0030
        return PlatformProbe(ServiceTiming.Default, time);
    }

    /// <summary>The info probe over the platform's loopback transport and socket connect.</summary>
    private static ServiceInfoProbe PlatformProbe(ServiceTiming timing, TimeProvider time) =>
        new(timing, new LoopbackTransport(new LoopbackSocketConnector()), time);

    /// <summary>The non-owning handle over a registered service.</summary>
    private static OpenCodeServer SharedService(ServiceRegistration registration) =>
        new(
            registration.Endpoint,
            registration.Password,
            registration.ProcessId);

    /// <summary>
    /// The CLI's <c>shutdownPersistentPty</c>: discovers the background service and shuts down its
    /// persistent-terminal daemon and every terminal it owns, without stopping the service.
    /// </summary>
    /// <param name="options">The same options as <see cref="DiscoverAsync"/>; a null <see cref="OpenCodeServerDiscoverOptions.ExpectedVersion"/> accepts any version.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>A task that completes when the service answered the shutdown, or without sending a shutdown request when discovery finds no usable service.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves for an XDG fallback.</exception>
    /// <exception cref="OpenCodeApiException">The service refused the shutdown.</exception>
    /// <exception cref="OpenCodeTransportException">The service could not be reached or answered a malformed body.</exception>
    /// <remarks>
    /// Discovers exactly as <see cref="DiscoverAsync"/> does. With no usable service it sends nothing
    /// and completes; otherwise it sends one persistent-terminal shutdown to the registered endpoint
    /// with the registration's credential, no location and no request bound of its own.
    /// </remarks>
    [Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
    public static Task ShutdownPersistentPtyAsync(
        OpenCodeServerDiscoverOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ShutdownPersistentPtyWithSeamsAsync(options,
            new ServiceDiscovery(new ServiceEnvironment(), new ServiceFileSystem(), SystemDiscoveryProbe()), new ServicePtyShutdown(), cancellationToken);

    /// <summary>The discovery-and-shutdown composition over injected seams.</summary>
    /// <param name="options">The discovery options.</param>
    /// <param name="discovery">The registration discovery.</param>
    /// <param name="shutdown">The persistent-terminal shutdown.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task completing after shutdown, or without a request when no usable service exists.</returns>
    internal static async Task ShutdownPersistentPtyWithSeamsAsync(
        OpenCodeServerDiscoverOptions? options,
        ServiceDiscovery discovery,
        IServicePtyShutdown shutdown,
        CancellationToken cancellationToken)
    {
        var registration = await discovery.DiscoverAsync(options, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return;
        }

        await shutdown.ShutdownAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the registered background service the way <c>opencode service stop</c> does: resolves
    /// the registration the options name (channel rules and legacy migration included), clears the
    /// handoff sidecar by default or attempts a best-effort handoff with <see cref="OpenCodeServerStopOptions.Pty"/>, then ends the registered process — a request to stop first, a
    /// hard kill when it survives — and removes the registration once the process is gone. The
    /// registration is re-read before the first signal and before the removal, so a service that
    /// re-registered after the stop read the registration is never signalled and a successor's
    /// registration is never removed; the hard kill follows the signalled process alone. Every
    /// signal compares the process's identity (pid and start time), so a pid the operating system
    /// reused after the stop first looked at it is never signalled. A missing or corrupt registration
    /// completes successfully; the shared service is shared, and this call is the one deliberate
    /// way to end it — disposing a discovered handle never does.
    /// </summary>
    /// <param name="options">The stop options; null stops the shared release registration.</param>
    /// <param name="cancellationToken">The caller's token. Cancellation before the first signal prevents it; after a signal it ends the bounded wait and leaves the registration in place.
    /// In handoff mode, a cancellation during the handoff can still leave a published ticket, or terminals already shut down, while the service keeps running.</param>
    /// <returns>A task that completes when the registered process is gone (reaped, when it is a child of this process) or there was none to stop.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves for an XDG fallback, or the registered process is still running after the hard kill.</exception>
    public static Task StopAsync(
        OpenCodeServerStopOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var timing = ServiceTiming.Default;
#pragma warning disable RS0030 // The stop composition root chooses the system clock; its handoff and terminator share it.
        var time = TimeProvider.System;
#pragma warning restore RS0030
        var fileSystem = new ServiceFileSystem();
        var stopper = new ServiceStopper(
            new ServiceEnvironment(),
            fileSystem,
            new ServicePtyHandoff(fileSystem, time, new ServicePtyShutdown()),
            new ServiceProcessControl(new ChildExitStatusReader()),
            timing,
            time);
        return stopper.StopAsync(options, cancellationToken);
    }

    /// <summary>
    /// Ensures a healthy compatible background service is running: reuses a ready daemon,
    /// replaces a version-mismatched one according to
    /// <see cref="OpenCodeServerEnsureOptions.VersionPolicy"/>, and otherwise spawns detached
    /// contenders until one registers or the wall-clock bound expires. The returned handle does
    /// not own the process; disposal is a no-op, and <see cref="StopAsync"/> is the one
    /// deliberate way to end the shared service. A service this call starts resumes every session
    /// the shared data marks as suspended, including one another process is still running, so both
    /// then drive it; don't let it start one while another process runs sessions on the same data.
    /// </summary>
    /// <param name="options">The ensure options; null uses every default, including
    /// <c>opencode serve --service</c> and <see cref="OpenCodeServerVersionPolicy.Ignore"/>.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>A non-owning handle over the ready service.</returns>
    /// <exception cref="ArgumentException">An option is blank, the registration path is relative, the command is empty, a command or environment entry contains a NUL, <c>Replace</c> or <c>Error</c> is set without <c>ExpectedVersion</c>, or the options contradict one another.</exception>
    /// <exception cref="OpenCodeServerException">No user home directory resolves, the election timed out, a contender or the registered service failed to start, the registered service speaks an incompatible health protocol at a version the call accepts, a version mismatch was refused, or a spawned command could not be resolved.</exception>
    public static Task<OpenCodeServer> EnsureAsync(
        OpenCodeServerEnsureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable RS0030 // The ensure composition root chooses the system clock; election collaborators and spawned contenders receive it.
        var time = TimeProvider.System;
#pragma warning restore RS0030
        return EnsureWithSeamsAsync(options, ServiceTiming.Default, new ServiceContenderSpawner(new PosixSpawn(), new WindowsSpawn(), time), time, cancellationToken);
    }

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
    /// <param name="time">The clock shared by the election collaborators.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>A non-owning handle over the ready service.</returns>
    internal static async Task<OpenCodeServer> EnsureWithSeamsAsync(
        OpenCodeServerEnsureOptions? options,
        ServiceTiming timing,
        IServiceContenderSpawner spawner,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(time);

        var fileSystem = new ServiceFileSystem();
        var ensurer = new ServiceEnsurer(
            new ServiceEnvironment(),
            fileSystem,
            PlatformProbe(timing, time),
            spawner,
            new ServicePtyHandoff(fileSystem, time, new ServicePtyShutdown()),
            new ServiceProcessControl(new ChildExitStatusReader()),
            time,
            new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()),
            timing);
        var registration = await ensurer.EnsureAsync(options, cancellationToken).ConfigureAwait(false);
        return SharedService(registration);
    }
}
