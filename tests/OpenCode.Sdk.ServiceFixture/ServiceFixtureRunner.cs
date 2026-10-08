using System.Globalization;

namespace OpenCode.Sdk.ServiceFixture;

/// <summary>
/// The isolated background-service entry point: runs one SDK door in a process whose environment
/// the launching test owns entirely, and reports the outcome on one stdout line. Split out of
/// <c>Program.cs</c>'s top-level statements to keep the dispatch under the repository's method-size
/// gates, the way the sandbox does; each mode's body lives in its own class.
/// </summary>
/// <remarks>
/// Modes: <c>discover-default [request-timeout-ms]</c> reads the shared release registration
/// through every default, probing under the pinned request bound or the one named;
/// <c>discover-channel &lt;channel&gt;</c> names a service channel; <c>ensure-channel
/// &lt;channel&gt; &lt;ledger&gt;</c> ensures the channel's service with the default <c>opencode serve
/// --service</c> command resolved from this process's PATH, recording every contender in the
/// ledger; <c>stop-channel &lt;channel&gt;</c> stops the channel's
/// registered service; <c>idle</c> prints <c>ready</c> and lingers until it is ended;
/// <c>ignore-sigterm</c> lingers the same way but ignores <c>SIGTERM</c> where the platform can
/// deliver one; <c>contender-probe</c> plays the contender the Ensure loop spawns, observed through
/// the spawner's stderr pipe, with <c>stall</c> and <c>stale</c> daemon stand-ins for the live
/// recovery proofs; <c>launcher-host &lt;command…&gt;</c> owns one standalone server started with that
/// command, prints its pid and what the launcher observes of it, and ends it once its own stdin
/// closes; <c>tree-root</c> starts an <c>idle</c> child, prints <c>ready child=&lt;pid&gt;</c>, and
/// lingers, the tree a tree-kill proof ends. The credential is never printed. Exit 0 carries an answer, 1 a failure, 2 a usage error.
/// </remarks>
internal static class ServiceFixtureRunner
{
    public static Task<int> RunAsync(string[] args) =>
        args switch
        {
            ["discover-default"] => DiscoveryMode.RunAsync(options: null),
            ["discover-default", var bound] when TryReadMilliseconds(bound) is { } requestTimeout => DiscoveryMode.RunAsync(options: null, requestTimeout),
            ["discover-channel", var channel] => DiscoveryMode.RunAsync(new OpenCodeServerDiscoverOptions { Channel = channel }),
            ["ensure-channel", var channel, var ledger] => EnsureMode.RunAsync(channel, ledger),
            ["stop-channel", var channel] => StopMode.RunAsync(channel),
            ["idle"] => LingeringProcessMode.RunAsync(ignoreTerminate: false),
            ["ignore-sigterm"] => LingeringProcessMode.RunAsync(ignoreTerminate: true),
            ["tree-root"] => TreeRootMode.RunAsync(),
            ["contender-probe", var probe, .. var arguments] => ContenderProbe.RunAsync(probe, arguments),
            ["launcher-host", .. var command] when command.Length > 0 => LauncherHostMode.RunAsync(command),
            _ => UsageAsync(),
        };

    private static TimeSpan? TryReadMilliseconds(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;

    private static async Task<int> UsageAsync()
    {
        await Console.Error
            .WriteLineAsync("Usage: discover-default [request-timeout-ms] | discover-channel <channel> | ensure-channel <channel> <ledger> | stop-channel <channel> | idle | ignore-sigterm | tree-root | contender-probe echo-argv-env [name …] | stderr-fill [bytes] | daemon-sleep | stall | stale | console-report | group-report | group-member | launcher-host <command …>")
            .ConfigureAwait(false);
        return 2;
    }
}
