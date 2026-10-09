using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// A bounded wait on a launched child's exit as the launcher observed it: the exit watch's own
/// task, which the watch completes when the child exits, so a test awaits it under a bound of its
/// own rather than for as long as the child might live.
/// </summary>
internal static class ChildExitObservation
{
    /// <summary>Waits at most <paramref name="bound"/> for the exit an exit watch observes.</summary>
    /// <param name="watch">The watch.</param>
    /// <param name="bound">How long the test waits.</param>
    /// <returns>The exit.</returns>
    /// <exception cref="TimeoutException">The child had not exited inside the bound.</exception>
    public static async Task<ChildExitStatus> WithinAsync(PosixChildExitWatch watch, TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(watch);
        return await watch.Exited.WaitAsync(bound);
    }

    /// <summary>Waits at most <paramref name="bound"/> for the exit a Windows exit watch observes.</summary>
    /// <param name="watch">The watch.</param>
    /// <param name="bound">How long the test waits.</param>
    /// <returns>The exit.</returns>
    /// <exception cref="TimeoutException">The child had not exited inside the bound.</exception>
    public static async Task<ChildExitStatus> WithinAsync(IWindowsExitWatch watch, TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(watch);
        return await watch.Exited.WaitAsync(bound);
    }

    /// <summary>Waits at most <paramref name="bound"/> for the exit of the child a server owns.</summary>
    /// <param name="server">The started server.</param>
    /// <param name="bound">How long the test waits.</param>
    /// <returns>The exit.</returns>
    /// <exception cref="TimeoutException">The child had not exited inside the bound.</exception>
    public static async Task<ChildExitStatus> WithinAsync(OpenCodeServer server, TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(server);
        return await server.ChildExited.WaitAsync(bound);
    }
}
