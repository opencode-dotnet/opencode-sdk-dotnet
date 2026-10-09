namespace OpenCode.Sdk.Internal.Launcher.Abstractions;

/// <summary>
/// How a platform ends its server, on upstream's rungs for that platform. The shared flow runs it
/// at most once per server, from whichever path ends the server first.
/// </summary>
internal interface IServerLadder
{
    /// <summary>Runs the ladder; bounded at every step, and it raises nothing.</summary>
    /// <param name="grace">The configured grace between the request to stop and the forced end.</param>
    /// <returns>A task that completes once the ladder has run to its last applicable rung.</returns>
    public Task RunAsync(TimeSpan grace);
}
