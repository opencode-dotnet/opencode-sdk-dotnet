using System.Globalization;
using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.ServiceFixture;

/// <summary>
/// A host that owns one standalone server, for the proofs that need the server's owner to be a
/// process the test can signal or kill: starts the server through <see cref="OpenCodeServer.StartAsync"/>
/// with the given command, prints <c>server pid=&lt;pid&gt;</c>, then
/// <c>children reaped automatically=&lt;True|False&gt;</c> as the launcher reads it from this host's
/// <c>SIGCHLD</c> action, and <c>server exit=&lt;how it ended&gt;</c> once the launcher observed the
/// server's exit. It holds the server until its own stdin reaches end-of-stream, which is how the
/// test ends it; disposal then ends the server.
/// </summary>
internal static class LauncherHostMode
{
    /// <summary>How long the host waits for the exit report once disposal returned.</summary>
    private static readonly TimeSpan ExitReportBound = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(IReadOnlyList<string> command)
    {
        var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions { Command = [.. command] }).ConfigureAwait(false);
        Task exitReport;
        await using (server.ConfigureAwait(false))
        {
            await ReportAsync("server pid=" + server.ProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await ReportAsync("children reaped automatically=" + new ChildExitStatusReader().AreChildrenReapedAutomatically().ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            exitReport = ReportExitAsync(server);
            _ = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        }

        await exitReport.WaitAsync(ExitReportBound).ConfigureAwait(false);
        return 0;
    }

    private static async Task ReportExitAsync(OpenCodeServer server)
    {
        var exit = await server.ChildExited.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        await ReportAsync("server exit=" + exit.Describe()).ConfigureAwait(false);
    }

    private static async Task ReportAsync(string line)
    {
        await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}
