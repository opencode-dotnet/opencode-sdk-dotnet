using System.ComponentModel;
using System.Diagnostics;
using OpenCode.Sdk.Internal.Abstractions;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Ends a launched server's whole process tree and reports what the kill achieved, never raising:
/// every outcome of the platform's kill, a partial one included, is a result the caller's
/// teardown continues from.
/// </summary>
internal sealed class ProcessTreeTerminator
{
    private readonly IProcessTreeKill _kill;

    /// <summary>Initializes a terminator over a whole-tree kill.</summary>
    /// <param name="kill">The platform kill the terminator issues.</param>
    public ProcessTreeTerminator(IProcessTreeKill kill)
    {
        ArgumentNullException.ThrowIfNull(kill);
        _kill = kill;
    }

    /// <summary>Gets the terminator over the shipped platform kill.</summary>
    public static ProcessTreeTerminator Platform { get; } = new(new PlatformProcessTreeKill());

    /// <summary>Ends the process tree.</summary>
    /// <param name="process">The tree's root, started by this process.</param>
    /// <returns>What the kill achieved; see <see cref="ProcessTreeKillResult"/>.</returns>
    public ProcessTreeKillResult TryKill(Process process)
    {
        try
        {
            return _kill.Kill(process);
        }
        catch (AggregateException)
        {
            // The runtime's tree kill ran, but at least one process of the tree refused it (a
            // signal or TerminateProcess failure); the processes it could reach were still ended.
            return ProcessTreeKillResult.Incomplete;
        }
        catch (InvalidOperationException)
        {
            // Already exited (or never started): there is no tree left to end, which is the same
            // end state the kill was asking for.
            return ProcessTreeKillResult.NothingToEnd;
        }
        catch (Win32Exception)
        {
            // Exited between the check and the kill, or the tree is already dying and its handle
            // is no longer accessible; either way this process has nothing further to issue.
            return ProcessTreeKillResult.NothingToEnd;
        }
    }
}
