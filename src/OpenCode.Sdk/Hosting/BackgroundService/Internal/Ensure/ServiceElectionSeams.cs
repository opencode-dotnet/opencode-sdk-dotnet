namespace OpenCode.Sdk.Internal;

/// <summary>
/// What one Ensure election runs against: the seams it drives and the timing it keeps. Built once
/// per call by <see cref="ServiceEnsurer"/>; the election owns only its own loop state.
/// </summary>
/// <param name="Probe">The info probe.</param>
/// <param name="Spawner">The detached contender spawn.</param>
/// <param name="Handoff">The persistent-terminal handoff sidecar.</param>
/// <param name="Terminator">The stop ladder the recovery and the replacement end a daemon with.</param>
/// <param name="FileSystem">The registration and service-config file access.</param>
/// <param name="Time">The clock for polling and the UTC deadline and spawn stamps.</param>
/// <param name="Executables">The launcher's executable resolution for the command's first entry.</param>
/// <param name="Timing">The lifecycle timing.</param>
internal sealed record ServiceElectionSeams(
    IServiceInfoProbe Probe,
    IServiceContenderSpawner Spawner,
    IServicePtyHandoff Handoff,
    ServiceTerminator Terminator,
    IServiceFileSystem FileSystem,
    TimeProvider Time,
    ExecutableResolver Executables,
    ServiceTiming Timing);
