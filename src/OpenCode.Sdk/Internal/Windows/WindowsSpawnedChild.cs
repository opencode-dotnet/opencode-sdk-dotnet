using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// A child an <see cref="Abstractions.IWindowsSpawn"/> started: its pid, the process handle that
/// pins that pid while it is open, the main thread of a child created suspended, and the parent's
/// end of each standard stream routed to a pipe, null for the others. Every member passes to the
/// caller, which owns it from here.
/// </summary>
internal sealed record WindowsSpawnedChild
{
    /// <summary>Gets the child's pid.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Gets the child's process handle; Windows reuses no pid while a handle to its process is open.</summary>
    public required SafeProcessHandle Process { get; init; }

    /// <summary>Gets the main thread of a child created suspended; null otherwise, the handle already closed.</summary>
    public SafeThreadHandle? MainThread { get; init; }

    /// <summary>Gets the write end of the child's standard input, when it was routed to a pipe.</summary>
    public Stream? StandardInput { get; init; }

    /// <summary>Gets the read end of the child's standard output, when it was routed to a pipe.</summary>
    public Stream? StandardOutput { get; init; }

    /// <summary>Gets the read end of the child's standard error, when it was routed to a pipe.</summary>
    public Stream? StandardError { get; init; }
}
