using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.TestSupport;
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
/// command, closes its own write end, and prints <c>inherited pipe eof=&lt;True|False&gt;</c>: the pipe's
/// end-of-stream arrives at once unless a server inherited the write end, which keeps it away while
/// the server runs, and only that server meets the probe's hang guard and the <c>False</c> report.
/// <c>handle-cycles &lt;command…&gt;</c> starts and disposes servers with that command, and fails starts
/// on purpose, in three rounds, and prints <c>census=&lt;after warm-up&gt;; &lt;after one round&gt;; &lt;after two&gt;</c>,
/// the census of the launcher's handle kinds once every exit watch and its duplicate were released,
/// after <c>baseline=</c>, the same census before the first launch
/// (<see cref="LauncherHandleCensus"/>), and <c>handles=</c> with this process's whole handle count
/// at the same points, for the record.
/// <c>handle-census &lt;foreign pid&gt;</c> runs the census's own controls against that process and
/// prints <c>before=</c>, <c>open=</c>, <c>closed=</c>, <c>mine-open=</c>, <c>mine-closed=</c>,
/// <c>parked=</c> censuses and <c>raw=</c>
/// whole handle counts (<see cref="ProveCensusAsync"/>).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsLauncherProbe
{
    /// <summary><c>JobObjectExtendedLimitInformation</c>.</summary>
    private const int ExtendedLimitInformation = 9;

    private const string ReadyLine = "{\"url\":\"http://127.0.0.1:1\"}";

    /// <summary>
    /// The isolation probe's hang guard: a released pipe ends at once, so only a server holding the
    /// inherited write end, which keeps the end-of-stream away for as long as it runs, reaches it.
    /// </summary>
    private static readonly TimeSpan EndOfStreamGuard = TimeSpan.FromSeconds(60);

    /// <summary>How long a round of the handle count waits for its exit watches to be released.</summary>
    private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(10);

    private const int CyclesPerRound = 10;

    /// <summary><c>STD_OUTPUT_HANDLE</c>.</summary>
    private const int StandardOutputHandle = -11;

    /// <summary><c>PROCESS_QUERY_LIMITED_INFORMATION</c>.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary><c>THREAD_QUERY_LIMITED_INFORMATION</c>.</summary>
    private const uint ThreadQueryLimitedInformation = 0x0800;

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

    public static async Task<int> ProbeHandleIsolationAsync(IReadOnlyList<string> command)
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var server = await OpenCodeServer.StartAsync(new OpenCodeServerOptions { Command = [.. command] }).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            pipe.DisposeLocalCopyOfClientHandle();
            var buffer = new byte[1];

            // The read end is synchronous, so this read holds a pool thread until the end-of-stream
            // or the process exit; the probe exits right after it reports. The server is released
            // only after the read, so an end-of-stream seen here came while the server ran; a
            // server holding the write end keeps it away until the guard.
            var read = pipe.ReadAsync(buffer, 0, buffer.Length);
            bool ended;
            try
            {
                ended = await read.WaitAsync(EndOfStreamGuard).ConfigureAwait(false) == 0;
            }
            catch (TimeoutException)
            {
                ended = false;
            }

            await Console.Out.WriteLineAsync("inherited pipe eof=" + ended.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }

        return 0;
    }

    public static async Task<int> CountHandlesAsync(IReadOnlyList<string> command)
    {
        // Before any launch: what this host holds of the counted kinds that no launcher path made,
        // such as a handle its own parent let it inherit.
        var baseline = LauncherHandleCensus.Take();
        var censuses = new List<LauncherHandles>(3);
        var counts = new List<int>(3);
        for (var round = 0; round < 3; round++)
        {
            await RunCyclesAsync(command, round == 0 ? 3 : CyclesPerRound).ConfigureAwait(false);
            censuses.Add(await SettledCensusAsync().ConfigureAwait(false));
            using var self = Process.GetCurrentProcess();
            counts.Add(self.HandleCount);
        }

        await Console.Out.WriteLineAsync("baseline=" + baseline).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("census=" + string.Join("; ", censuses)).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("handles=" + string.Join(", ", counts.Select(static count => count.ToString(CultureInfo.InvariantCulture)))).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The census's own controls, in a host where nothing else opens handles. Positive: a handle on
    /// another process, a <c>SYNCHRONIZE</c>-only one, a handle on one of its threads, a pipe (two
    /// ends), and a job each show in their own count while open, and none once closed. Negative: two
    /// threads parked in this process add to its whole handle count and not to the census.
    /// </summary>
    /// <param name="foreignProcessId">A process other than this one that outlives the probe.</param>
    /// <returns>0 once every census is printed.</returns>
    public static async Task<int> ProveCensusAsync(int foreignProcessId)
    {
        int threadId;
        using (var foreign = Process.GetProcessById(foreignProcessId))
        {
            threadId = foreign.Threads[0].Id;
        }

        // The first pipe a process creates leaves one pipe handle of the system's own behind, held
        // for the process's lifetime (measured: one more after both ends closed, never again after),
        // so one pipe opened and closed first keeps it out of the counted window, as the launcher's
        // warm-up round does in handle-cycles.
        CloseAll(OpenPipe());

        var before = LauncherHandleCensus.Take();
        var mine = OpenOneOfEachKind(foreignProcessId, threadId);
        LauncherHandles open;
        LauncherHandles mineOpen;
        try
        {
            open = LauncherHandleCensus.Take();
            mineOpen = LauncherHandleCensus.Count(mine);
        }
        finally
        {
            CloseAll(mine);
        }

        // The same values once closed: the race a census meets when the finalizer closes a handle
        // between the snapshot and the questions about it.
        var mineClosed = LauncherHandleCensus.Count(mine);
        var closed = LauncherHandleCensus.Take();
        var (whileParked, rawBefore, rawParked) = CensusWhileThreadsParked();

        await Console.Out.WriteLineAsync("before=" + before).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("open=" + open).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("closed=" + closed).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("mine-open=" + mineOpen).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("mine-closed=" + mineClosed).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("parked=" + whileParked).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            "raw=" + rawBefore.ToString(CultureInfo.InvariantCulture) + ", " + rawParked.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Opens, as raw handle values, one handle of each kind the census counts: one on the foreign
    /// process, a <c>SYNCHRONIZE</c>-only one, one on its thread, both ends of a pipe, and a job.
    /// </summary>
    private static (IntPtr Handle, uint Access)[] OpenOneOfEachKind(int foreignProcessId, int threadId)
    {
        var opened = new List<(IntPtr Handle, uint Access)>(6);
        try
        {
            opened.Add((Opened(OpenProcess(ProcessQueryLimitedInformation, inherit: false, (uint)foreignProcessId)), ProcessQueryLimitedInformation));
            opened.Add((Opened(OpenProcess(WindowsInterop.Synchronize, inherit: false, (uint)foreignProcessId)), WindowsInterop.Synchronize));
            opened.Add((Opened(OpenThread(ThreadQueryLimitedInformation, inherit: false, (uint)threadId)), ThreadQueryLimitedInformation));
            opened.AddRange(OpenPipe());
            opened.Add((Opened(CreateJobObject(IntPtr.Zero, IntPtr.Zero)), 0));
            return [.. opened];
        }
        catch
        {
            CloseAll(opened);
            throw;
        }
    }

    private static (IntPtr Handle, uint Access)[] OpenPipe() =>
        CreatePipe(out var read, out var write, IntPtr.Zero, 0)
            ? [(read, 0), (write, 0)]
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    private static IntPtr Opened(IntPtr handle) =>
        handle == IntPtr.Zero ? throw new Win32Exception(Marshal.GetLastWin32Error()) : handle;

    private static void CloseAll(IEnumerable<(IntPtr Handle, uint Access)> handles)
    {
        foreach (var (handle, _) in handles)
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>Parks two threads, counts while they wait, and releases them.</summary>
    private static (LauncherHandles Census, int RawBefore, int RawParked) CensusWhileThreadsParked()
    {
        using var release = new ManualResetEventSlim();
        var parked = Enumerable.Range(0, 2).Select(_ => new Thread(() => release.Wait())).ToArray();
        int rawBefore;
        using (var self = Process.GetCurrentProcess())
        {
            rawBefore = self.HandleCount;
        }

        foreach (var worker in parked)
        {
            worker.Start();
        }

        var census = LauncherHandleCensus.Take();
        int rawParked;
        using (var self = Process.GetCurrentProcess())
        {
            rawParked = self.HandleCount;
        }

        release.Set();
        foreach (var worker in parked)
        {
            worker.Join();
        }

        return (census, rawBefore, rawParked);
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

    /// <summary>
    /// Waits for the round's exit watches to be released, then for their <c>SYNCHRONIZE</c>
    /// duplicates to close, and counts. Only the duplicates are awaited: a released watch hands its
    /// duplicate back to the pool's wait thread, which closes it once it processes the
    /// unregistration, after the watch is uncounted. Every other kind is counted once, so a handle a
    /// round left behind stays in the census rather than being waited out.
    /// </summary>
    [SlopwatchSuppress(
        "SW004",
        "An exit watch is released on the pool a moment after its child exits, and its duplicate closes on the wait thread after that, with no signal to await for either: the polls wait for the live count and then the duplicates to reach zero, bounded together by ReleaseBound.")]
    private static async Task<LauncherHandles> SettledCensusAsync()
    {
        var waited = Stopwatch.StartNew();
        while (WindowsExitWatch.LiveWatches != 0 && waited.Elapsed < ReleaseBound)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        var census = LauncherHandleCensus.Take();
        while (census.ExitWaits != 0 && waited.Elapsed < ReleaseBound)
        {
            await Task.Delay(50).ConfigureAwait(false);
            census = LauncherHandleCensus.Take();
        }

        return census;
    }

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, uint size);

    [DllImport("kernel32", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, IntPtr name);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

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
