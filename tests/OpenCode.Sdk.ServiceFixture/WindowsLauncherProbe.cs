using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenCode.Sdk.Internal.Windows;
using Testably.Abstractions;

namespace OpenCode.Sdk.ServiceFixture;

/// <summary>
/// The Windows launcher probes: processes that play the server, or the server's owner, and report
/// what the launcher gave them. Each server stand-in accepts and ignores the launcher's trailing
/// <c>--stdio --port 0</c>.
/// </summary>
/// <remarks>
/// <c>job-report</c> reads, as its first act, the limits of the job it runs in directly (a nested
/// job's own limits, not those of a job above it) and prints <c>job limits=0x&lt;flags&gt;</c>, or
/// <c>job limits=none</c> outside any job, on stderr; then it prints the readiness line and lingers.
/// <c>exit-code &lt;n&gt;</c> prints <c>exiting</c> on stderr and exits with that code before readiness.
/// <c>close-stdout</c> reports its pid on stderr in <c>ladder-tree.js</c>'s form, closes its stdout
/// handle itself, which a Node or Bun stand-in cannot (its runtime keeps a handle of its own), and
/// lingers.
/// <c>handle-isolation &lt;command…&gt;</c> owns an inheritable pipe while it starts a server with that
/// command, closes its own write end, and prints <c>inherited pipe eof=&lt;True|False&gt;</c>: only a
/// server that inherited the write end keeps the end-of-stream away. <c>handle-cycles
/// &lt;command…&gt;</c> starts and disposes servers with that command, and fails starts on purpose, in
/// three rounds, and prints <c>handles=&lt;after warm-up&gt;, &lt;after one round&gt;, &lt;after two&gt;</c>:
/// this process's handle count once every exit watch was released.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsLauncherProbe
{
    /// <summary><c>JobObjectExtendedLimitInformation</c>.</summary>
    private const int ExtendedLimitInformation = 9;

    private const string ReadyLine = "{\"url\":\"http://127.0.0.1:1\"}";

    /// <summary>How long the isolation probe waits for the end-of-stream a released pipe gives.</summary>
    private static readonly TimeSpan EndOfStreamBound = TimeSpan.FromSeconds(2);

    /// <summary>How long a round of the handle count waits for its exit watches to be released.</summary>
    private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(10);

    private const int CyclesPerRound = 10;

    /// <summary><c>STD_OUTPUT_HANDLE</c>.</summary>
    private const int StandardOutputHandle = -11;

    private static readonly RealFileSystem FileSystem = new();

    /// <summary>Whether a process is attached to this process's console.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>True when the console lists it.</returns>
    public static bool SharesConsole(int processId)
    {
        var processes = new uint[64];
        var count = GetConsoleProcessList(processes, (uint)processes.Length);
        return processes.Take((int)Math.Min(count, (uint)processes.Length)).Contains((uint)processId);
    }

    public static async Task<int> ReportJobAsync()
    {
        var limits = new WindowsInterop.JobExtendedLimitInformation();
        var report = QueryInformationJobObject(
                IntPtr.Zero, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<WindowsInterop.JobExtendedLimitInformation>(), IntPtr.Zero)
            ? "job limits=0x" + limits.BasicLimitInformation.LimitFlags.ToString("X", CultureInfo.InvariantCulture)
            : "job limits=none";
        await Console.Error.WriteLineAsync(report).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(ReadyLine).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);

        // A gate nobody opens: the stand-in lingers until something ends it, which is the point.
        using var forever = new SemaphoreSlim(0);
        await forever.WaitAsync().ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> CloseStandardOutputAsync()
    {
        await Console.Error.WriteLineAsync("ladder-tree role=root pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await Console.Error.FlushAsync().ConfigureAwait(false);
        if (!CloseHandle(GetStdHandle(StandardOutputHandle)))
        {
            return 1;
        }

        // A gate nobody opens: the stand-in lingers with its stdout closed until something ends it.
        using var forever = new SemaphoreSlim(0);
        await forever.WaitAsync().ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> ExitWithCodeAsync(int code)
    {
        await Console.Error.WriteLineAsync("exiting").ConfigureAwait(false);
        await Console.Error.FlushAsync().ConfigureAwait(false);
        Environment.Exit(code);
        return code;
    }

    [SlopwatchSuppress(
        "SW004",
        "The bound is the proof: an end-of-stream that has not come within it is what a server holding an inherited write end looks like, and nothing else can signal its absence.")]
    public static async Task<int> ProbeHandleIsolationAsync(IReadOnlyList<string> command)
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions { Command = [.. command] }).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            pipe.DisposeLocalCopyOfClientHandle();
            var buffer = new byte[1];

            // The read end is synchronous, so this read holds a pool thread until the end-of-stream
            // or the process exit; the probe exits right after it reports.
            var read = pipe.ReadAsync(buffer, 0, buffer.Length);
            var ended = await Task.WhenAny(read, Task.Delay(EndOfStreamBound)).ConfigureAwait(false) == read && await read.ConfigureAwait(false) == 0;
            await Console.Out.WriteLineAsync("inherited pipe eof=" + ended.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }

        return 0;
    }

    public static async Task<int> CountHandlesAsync(IReadOnlyList<string> command)
    {
        var counts = new List<int>(3);
        for (var round = 0; round < 3; round++)
        {
            await RunCyclesAsync(command, round == 0 ? 3 : CyclesPerRound).ConfigureAwait(false);
            counts.Add(await SettledHandleCountAsync().ConfigureAwait(false));
        }

        await Console.Out.WriteLineAsync("handles=" + string.Join(", ", counts.Select(static count => count.ToString(CultureInfo.InvariantCulture)))).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task RunCyclesAsync(IReadOnlyList<string> command, int cycles)
    {
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions { Command = [.. command] }).ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            await FailedStartAsync(new OpenCodeServerOptions { Command = [command[0], "-e", "process.exit(3)"] }).ConfigureAwait(false);
            await FailedStartAsync(new OpenCodeServerOptions
            {
                Command = [.. command],
                WorkingDirectory = FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "opencode-sdk-missing-" + Guid.NewGuid().ToString("N")),
            }).ConfigureAwait(false);
        }
    }

    [SlopwatchSuppress(
        "SW003",
        "The failure is the cycle's purpose: the round counts only what a failed start leaves behind, so the expected exception ends the step.")]
    private static async Task FailedStartAsync(OpenCodeServerOptions options)
    {
        try
        {
            var unexpected = await OpenCodeServer.StartAsync(options).ConfigureAwait(false);
            await unexpected.DisposeAsync().ConfigureAwait(false);
        }
        catch (OpenCodeServerException)
        {
            // The expected end of the step.
            return;
        }

        throw new InvalidOperationException("A start meant to fail succeeded.");
    }

    [SlopwatchSuppress(
        "SW004",
        "An exit watch is released on the pool a moment after its child exits, with no signal to await: the poll waits for the live count to reach zero, bounded by ReleaseBound.")]
    private static async Task<int> SettledHandleCountAsync()
    {
        var waited = Stopwatch.StartNew();
        while (WindowsExitWatch.LiveWatches != 0 && waited.Elapsed < ReleaseBound)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        using var self = Process.GetCurrentProcess();
        return self.HandleCount;
    }

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr job, int informationClass, ref WindowsInterop.JobExtendedLimitInformation information, uint length, IntPtr returnLength);
}
