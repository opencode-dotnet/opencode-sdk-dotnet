namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// A child a <see cref="Abstractions.IPosixSpawn"/> started: its pid and the parent's end of each
/// descriptor routed to a pipe, null for the others. Each stream passes to the caller, which owns
/// it from here; the child is the caller's to reap, since only its parent can.
/// </summary>
internal sealed record PosixSpawnedChild
{
    /// <summary>Gets the child's pid; it also names the child's session and process group, which the child leads.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Gets the write end of the child's standard input, when it was routed to a pipe.</summary>
    public Stream? StandardInput { get; init; }

    /// <summary>Gets the read end of the child's standard output, when it was routed to a pipe.</summary>
    public Stream? StandardOutput { get; init; }

    /// <summary>Gets the read end of the child's standard error, when it was routed to a pipe.</summary>
    public Stream? StandardError { get; init; }
}
