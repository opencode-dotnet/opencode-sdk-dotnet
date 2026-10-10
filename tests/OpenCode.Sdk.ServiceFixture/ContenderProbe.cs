using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenCode.Sdk.ServiceFixture;

/// <summary>
/// The <c>contender-probe</c> mode: a stand-in for the background service the pinned client's
/// Ensure loop spawns while a registration is unresolved (<c>spawnServiceContender</c>,
/// <c>packages/client/src/service-contender.ts</c> at the pin), started by a test through the SDK's
/// contender spawner seam. The spawner pipes only this process's stderr and gives the other two
/// standard streams nothing to do, so every observation rides stderr.
/// </summary>
/// <remarks>
/// Three seam probes, two daemon stand-ins, and the Windows console probes
/// <see cref="ContenderConsoleProbe"/> documents. <c>echo-argv-env &lt;name&gt;…</c> floods stdout
/// with <see cref="StdoutFloodBytes"/> bytes, probes whether stdin reads end at once, and then
/// prints one JSON line on stderr carrying this process's own arguments (everything after the mode
/// name) and the value of each named environment variable (<c>null</c> for one that is absent),
/// together with what the process can see of its standard streams; it exits 0.
/// <c>stderr-fill [bytes]</c> writes <c>bytes</c> (16 KiB by default) of a position-numbered,
/// whitespace-free pattern to stderr in 4 KiB chunks paced five milliseconds apart, and exits 0.
/// <c>daemon-sleep</c> prints <c>ready pid=&lt;pid&gt;</c> on stderr and then sleeps until a signal
/// ends it. <c>daemon-parent</c> starts a <c>daemon-sleep</c> of its own that writes to this
/// process's stderr, prints <c>spawned pid=&lt;pid&gt;</c> naming it, and exits at once: the short-lived
/// parent whose child outlives it. The daemon stand-ins print <c>ready pid=&lt;pid&gt; port=&lt;port&gt;</c> on stdout, so
/// a test can seed a registration naming them, and then play a registered service: <c>stall</c>
/// accepts every connection and never answers one, so the info probe times out, and <c>stale</c>
/// answers with an identity no registration carries, so the probe reads no service. Nothing here
/// touches the SDK or the registration file: the mode is the contender the seam starts, never a
/// seam consumer, so this executable builds without the spawner.
/// </remarks>
internal static class ContenderProbe
{
    /// <summary>
    /// The echo probe's stdout flood: far over every pipe buffer, so a child whose stdout is a
    /// pipe nobody reads blocks inside the flood and never reaches its JSON line.
    /// </summary>
    public const int StdoutFloodBytes = 80 * 1024;

    /// <summary>stderr-fill's block: ten digits of the block number, then the filler. No whitespace anywhere, so a consumer's trim can never change the content.</summary>
    private const int BlockBytes = 64;

    /// <summary>The block filler after the number: 54 characters, the exact half of a block that is not its number.</summary>
    private const string BlockFiller = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_-";

    /// <summary>stderr-fill's default: 256 blocks, so the spawner's final-8 KiB tail cuts across numbered block boundaries.</summary>
    public const int DefaultFillBytes = 16 * 1024;

    private const int ChunkBytes = 4 * 1024;

    /// <summary>The pacing between stderr-fill chunks: fast enough for the tail proof, slow enough for the release-drain proof to stay inside its survival window.</summary>
    private static readonly TimeSpan ChunkDelay = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// The stdin probe's hang guard: a NUL stdin answers end-of-file at once, so only a stdin that
    /// blocks reaches it. It leaves the probe inside the launching test's thirty-second wait for the
    /// report, so a blocked stdin still arrives as the <c>blocked</c> report.
    /// </summary>
    private static readonly TimeSpan StandardInputGuard = TimeSpan.FromSeconds(20);

    public static Task<int> RunAsync(string mode, IReadOnlyList<string> arguments) =>
        mode switch
        {
            "echo-argv-env" => EchoArgumentsAndEnvironmentAsync(arguments),
            "stderr-fill" => FillStandardErrorAsync(arguments),
            "daemon-sleep" => SleepLikeADaemonAsync(),
            "daemon-parent" => StartDaemonAndExitAsync(),
            "stall" => StallAsync(),
            "stale" => StaleAsync(),
            "console-report" or "group-report" or "group-member" => RunWindowsProbeAsync(mode),
            _ => UsageAsync(),
        };

    /// <summary>The console probes ask about Windows consoles and process groups, which no other platform has: there they are a usage error.</summary>
    private static Task<int> RunWindowsProbeAsync(string mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UsageAsync();
        }

        return mode switch
        {
            "console-report" => ContenderConsoleProbe.ReportConsoleAsync(),
            "group-report" => ContenderConsoleProbe.ReportGroupAsync(),
            _ => ContenderConsoleProbe.RunMemberAsync(),
        };
    }

    private static async Task<int> EchoArgumentsAndEnvironmentAsync(IReadOnlyList<string> names)
    {
        // The flood comes before the report: when the spawner gave this process an unread pipe
        // for stdout, the flood blocks here and the JSON never prints, which is the exact
        // failure the harness reads from the missing line.
        var flood = new byte[StdoutFloodBytes];
        Array.Fill(flood, (byte)'x');
        using (var standardOutput = Console.OpenStandardOutput())
        {
            await standardOutput.WriteAsync(flood, CancellationToken.None).ConfigureAwait(false);
            await standardOutput.FlushAsync().ConfigureAwait(false);
        }

        var stdin = await ProbeStandardInputAsync().ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            environment[name] = Environment.GetEnvironmentVariable(name);
        }

        var report = new
        {
            // The muxer names the fixture dll as argv[0], so the probe's own arguments begin
            // after [dll, contender-probe, echo-argv-env].
            argv = Environment.GetCommandLineArgs().Skip(3).ToArray(),
            env = environment,
            stdin,
            stdinIsRedirected = Console.IsInputRedirected,
            stdoutIsRedirected = Console.IsOutputRedirected,
            stdoutFlushed = StdoutFloodBytes,
        };
        await Console.Error.WriteLineAsync(JsonSerializer.Serialize(report)).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Reads one character of stdin: a NUL stdin answers EOF at once, a pipe the spawner left open
    /// blocks until someone writes, and an inherited console blocks for input. The read's own
    /// outcome decides the report; the hang guard turns only a read that blocks into the
    /// <c>blocked</c> report instead of a hang the test would meet only as a timeout.
    /// </summary>
    private static async Task<string> ProbeStandardInputAsync()
    {
        // A thread of its own for the blocking read, so the answer never waits for a pool thread.
        // A read that blocks keeps that background thread until the process exits.
        var read = Task.Factory.StartNew(
            static () => Console.In.Read(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            return await read.WaitAsync(StandardInputGuard).ConfigureAwait(false) < 0 ? "eof" : "byte";
        }
        catch (TimeoutException)
        {
            return "blocked";
        }
    }

    [SlopwatchSuppress(
        "SW004",
        "The delay paces the fill inside the test's survival window: unpaced output would still prove the tail, but the release-drain proof needs the writer alive across Release, and pipe backpressure alone does not schedule that.")]
    private static async Task<int> FillStandardErrorAsync(IReadOnlyList<string> arguments)
    {
        var bytes = DefaultFillBytes;
        if (arguments.Count > 0
            && (arguments.Count > 1
                || !int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out bytes)
                || bytes <= 0))
        {
            return await UsageAsync().ConfigureAwait(false);
        }

        // Numbered 64-byte blocks, flushed in 4 KiB chunks five milliseconds apart: the numbers
        // make the final tail's boundary checkable byte for byte, and the pace keeps the release
        // proof from finishing before its survival window closes.
        var builder = new StringBuilder(ChunkBytes);
        var remaining = bytes;
        var block = 0;
        while (remaining > 0)
        {
            var count = Math.Min(BlockBytes, remaining);
            var text = block.ToString("D10", CultureInfo.InvariantCulture) + BlockFiller;
            _ = builder.Append(count == BlockBytes ? text : text.AsSpan(0, count));
            remaining -= count;
            block++;
            if (builder.Length >= ChunkBytes || remaining == 0)
            {
                await Console.Error.WriteAsync(builder).ConfigureAwait(false);
                _ = builder.Clear();
                await Console.Error.FlushAsync().ConfigureAwait(false);
                await Task.Delay(ChunkDelay).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>
    /// Prints the ready line naming this process's pid on the one channel the contender seam
    /// reads, then sleeps. No signal handler is registered, so a <c>SIGTERM</c> (and the hard
    /// kill Windows makes of it) ends the process with the runtime's default action.
    /// </summary>
    private static async Task<int> SleepLikeADaemonAsync()
    {
        await Console.Error
            .WriteLineAsync($"ready pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        await Console.Error.FlushAsync().ConfigureAwait(false);

        // A gate nobody opens: the process lingers until a signal ends it, which is the point.
        using var forever = new SemaphoreSlim(0);
        await forever.WaitAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Starts a <c>daemon-sleep</c> of this fixture and exits without waiting for it. The child
    /// gets this process's stderr as its own, so its ready line rides the pipe the contender
    /// seam reads after this process is gone; its stdin is a pipe this process never writes,
    /// which is what makes the runtime hand the child explicit standard handles. On Windows a
    /// console-less parent's console child would otherwise take a console of its own, with that
    /// console's handles in place of the pipe, so the child gets a windowless console.
    /// </summary>
    private static async Task<int> StartDaemonAndExitAsync()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        info.ArgumentList.Add(typeof(ContenderProbe).Assembly.Location);
        info.ArgumentList.Add("contender-probe");
        info.ArgumentList.Add("daemon-sleep");
        using var daemon = Process.Start(info) ?? throw new InvalidOperationException("The daemon did not start.");
        await Console.Error
            .WriteLineAsync($"spawned pid={daemon.Id.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        await Console.Error.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> UsageAsync()
    {
        await Console.Error
            .WriteLineAsync("Usage: contender-probe echo-argv-env [name …] | stderr-fill [bytes] | daemon-sleep | daemon-parent | stall | stale | console-report | group-report | group-member")
            .ConfigureAwait(false);
        return 2;
    }

    /// <summary>
    /// Prints the ready line naming this process's pid and the loopback port it bound, the two
    /// facts a test needs to seed a registration for it, then accepts every connection and never
    /// answers one: the info probe's own request bound expires, which is the consecutive-timeout
    /// signal the ensure loop's recovery counts.
    /// </summary>
    private static async Task<int> StallAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await ReportDaemonReadyAsync(((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);

        var held = new List<TcpClient>();
        try
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                held.Add(client);
            }
        }
        finally
        {
            foreach (var client in held)
            {
                client.Dispose();
            }
        }

        return 0;
    }

    /// <summary>
    /// Prints the ready line naming this process's pid and the loopback port it bound, then answers
    /// every <c>/api/info</c> with this process's own identity at a version no release ever built
    /// (<c>0.0.0-stale</c>). A registration naming a different pid or version — the superseded
    /// record a restart leaves behind — reads as no service; a registration naming this identity
    /// reads as a ready service at the wrong version, which the ensure loop replaces.
    /// </summary>
    private static async Task<int> StaleAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await ReportDaemonReadyAsync(((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);

        await ServeSupersededAsync(listener).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Prints the single ready line a daemon stand-in test reads: this process's pid and the bound port.</summary>
    private static async Task ReportDaemonReadyAsync(int port)
    {
        await Console.Out
            .WriteLineAsync($"ready pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} port={port.ToString(CultureInfo.InvariantCulture)}")
            .ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Accepts and answers until a signal ends the process; the token is never cancelled, but the loop's break names that exit so the analyzer sees it.</summary>
    private static async Task ServeSupersededAsync(TcpListener listener)
    {
        using var shutdown = new CancellationTokenSource();
        while (!shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            await AnswerSupersededAsync(client).ConfigureAwait(false);
        }
    }

    /// <summary>Answers <c>/api/info</c> with this process's pid at the never-built <c>0.0.0-stale</c>
    /// version and any other route with an empty 404, the way the reference fixture does: the
    /// replacement's handoff prepare and shutdown read the 404 as "route absent" and proceed to
    /// terminate.</summary>
    [SlopwatchSuppress(
        "SW003",
        "Connection teardown: the probe aborting mid-exchange is the expected end of a refused handoff prepare, and a fixture stand-in has no handler to add.")]
    private static async Task AnswerSupersededAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
        {
            try
            {
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                string? header;
                do
                {
                    header = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                while (!string.IsNullOrEmpty(header));

                var isInfo = requestLine is not null && IsInfoPath(requestLine);
                var body = isInfo
                    ? "{\"pid\":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                        + ",\"version\":\"0.0.0-stale\"}"
                    : string.Empty;
                var response = "HTTP/1.1 " + (isInfo ? "200 OK" : "404 Not Found")
                    + "\r\nContent-Type: application/json\r\nContent-Length: "
                    + body.Length.ToString(CultureInfo.InvariantCulture)
                    + "\r\nConnection: close\r\n\r\n" + body;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The probe aborted mid-exchange: connection teardown, never a fixture failure.
            }
        }
    }

    /// <summary>Whether a request line asks for the info route.</summary>
    private static bool IsInfoPath(string requestLine)
    {
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 && string.Equals(parts[1], "/api/info", StringComparison.Ordinal);
    }
}
