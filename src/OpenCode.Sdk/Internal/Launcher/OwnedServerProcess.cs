using OpenCode.Sdk.Internal.Launcher.Abstractions;
using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// What a platform strategy hands the shared flow once its server runs: the pid, the stdin lease,
/// the output readers, the server's own exit, the platform's ladder, and whatever the platform
/// releases last. The flow owns each member from here.
/// </summary>
internal sealed record OwnedServerProcess
{
    /// <summary>Gets the pid of the process the start owns.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Gets the write end of the server's stdin: the ownership lease, never written, closed on release.</summary>
    public required Stream? StandardInput { get; init; }

    /// <summary>Gets the readers of the server's stdout and stderr.</summary>
    public required PipeOutputPump Output { get; init; }

    /// <summary>Gets the server's own exit, never the end of its output.</summary>
    public required Task<ChildExitStatus> Exited { get; init; }

    /// <summary>Gets the platform's ladder.</summary>
    public required IServerLadder Ladder { get; init; }

    /// <summary>Gets what the platform releases after everything else; null when it has nothing.</summary>
    public IDisposable? Resources { get; init; }
}
