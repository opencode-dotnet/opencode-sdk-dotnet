using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace OpenCode.Sdk.ServiceFixture;

/// <summary>
/// The Windows console probes of the <c>contender-probe</c> mode: what a contender can see of the
/// console, show state, and process group its spawner gave it. Each prints one JSON line on
/// stderr, the one channel the contender seam reads, and exits 0.
/// </summary>
/// <remarks>
/// <c>console-report</c> reads, before anything else runs, whether the process is attached to a
/// console (<c>GetConsoleProcessList</c> fails without one), whether that console has a window,
/// and the flags and show state of the startup info it was created with.
/// <c>group-report</c> answers whether this process roots its own process group. Windows has no
/// call that names a process's group, so the probe asks the console instead: it starts a
/// <c>group-member</c> of its own in a new process group with a new windowless console, attaches
/// to that console, and generates <c>CTRL_BREAK_EVENT</c> for the group its own pid names. Only
/// processes of that group on that console may receive it: when this process roots the group, it
/// alone hears the event and the member, rooted in another group, does not. When it does not root
/// one, its pid names no group: the console refuses the call (<c>sendError</c> carries the
/// platform error, <c>ERROR_INVALID_PARAMETER</c> where measured), and a console that treated an
/// unknown group like group 0 instead would deliver the event to the member as well. Either way
/// the pair of answers differs from the rooted one. The console is the member's, never the
/// launching host's, so nothing outside the probe can receive the event.
/// <c>group-member</c> prints <c>ready</c> on stdout, prints <c>break</c> for each
/// <c>CTRL_BREAK_EVENT</c> it hears instead of ending, and exits when stdin closes.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ContenderConsoleProbe
{
    /// <summary><c>CTRL_BREAK_EVENT</c>: unlike Ctrl+C, a new process group never starts with it ignored.</summary>
    private const uint ControlBreakEvent = 1;

    /// <summary>How long a probe waits for a console event before it reports the event as not delivered.</summary>
    private static readonly TimeSpan EventBound = TimeSpan.FromSeconds(3);

    /// <summary>How long the probe waits for its member to start and to end.</summary>
    private static readonly TimeSpan MemberBound = TimeSpan.FromSeconds(30);

    public static async Task<int> ReportConsoleAsync()
    {
        var processes = new uint[4];
        var attached = GetConsoleProcessList(processes, (uint)processes.Length) > 0;
        var window = GetConsoleWindow() != IntPtr.Zero;
        GetStartupInfo(out var startup);

        var report = new
        {
            attached,
            window,
            startupFlags = startup.Flags,
            showWindow = startup.ShowWindow,
        };
        await Console.Error.WriteLineAsync(JsonSerializer.Serialize(report)).ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> ReportGroupAsync()
    {
        // The writer binds the stderr pipe now, before the console attach could change what the
        // standard handles name.
        var error = Console.Error;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memberHeard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var member = StartMember(line =>
        {
            if (string.Equals(line, "ready", StringComparison.Ordinal))
            {
                _ = ready.TrySetResult();
            }
            else if (string.Equals(line, "break", StringComparison.Ordinal))
            {
                _ = memberHeard.TrySetResult();
            }
        });
        try
        {
            if (!await CompletesWithinAsync(ready.Task, MemberBound).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The group member did not report ready.");
            }

            if (!AttachConsole((uint)member.Id))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AttachConsole to the group member failed");
            }

            var rootHeard = false;
            var sendError = 0;
            try
            {
                var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context =>
                       {
                           context.Cancel = true;
                           _ = heard.TrySetResult();
                       }))
                {
                    // A refusal is an answer, not a probe failure: the console refuses a group id
                    // that roots no group on it.
                    if (GenerateConsoleCtrlEvent(ControlBreakEvent, (uint)Environment.ProcessId))
                    {
                        rootHeard = await CompletesWithinAsync(heard.Task, EventBound).ConfigureAwait(false);
                    }
                    else
                    {
                        sendError = Marshal.GetLastWin32Error();
                    }
                }
            }
            finally
            {
                _ = FreeConsole();
            }

            var report = new
            {
                memberPid = member.Id,
                sendError,
                rootHeard,
                memberHeard = await CompletesWithinAsync(memberHeard.Task, EventBound).ConfigureAwait(false),
            };
            await error.WriteLineAsync(JsonSerializer.Serialize(report)).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await EndMemberAsync(member).ConfigureAwait(false);
        }
    }

    public static async Task<int> RunMemberAsync()
    {
        using var registration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, static context =>
        {
            context.Cancel = true;
            Console.Out.WriteLine("break");
            Console.Out.Flush();
        });
        await Console.Out.WriteLineAsync("ready").ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
        _ = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>The member: this fixture again, in a new process group with a new windowless console and all three standard streams piped; each stdout line goes to <paramref name="onLine"/>.</summary>
    private static Process StartMember(Action<string> onLine)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            CreateNewProcessGroup = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(typeof(ContenderConsoleProbe).Assembly.Location);
        info.ArgumentList.Add("contender-probe");
        info.ArgumentList.Add("group-member");
        var member = new Process { StartInfo = info };
        member.OutputDataReceived += (_, line) =>
        {
            if (line.Data is { } data)
            {
                onLine(data);
            }
        };
        try
        {
            _ = member.Start();
            member.BeginOutputReadLine();
            member.BeginErrorReadLine();
            return member;
        }
        catch
        {
            member.Dispose();
            throw;
        }
    }

    /// <summary>Closes the member's stdin so it exits on its own, and ends it hard if it has not within the bound.</summary>
    private static async Task EndMemberAsync(Process member)
    {
        try
        {
            member.StandardInput.Close();
        }
        catch (IOException)
        {
            // The pipe broke, so closing it cannot be the member's cue to exit: end it hard now.
            member.Kill(entireProcessTree: true);
        }

        using var bound = new CancellationTokenSource(MemberBound);
        try
        {
            await member.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            member.Kill(entireProcessTree: true);
            await member.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan bound)
    {
        try
        {
            await task.WaitAsync(bound).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);

    [DllImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32", EntryPoint = "GetStartupInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void GetStartupInfo(out StartupInfo startupInfo);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    /// <summary><c>STARTUPINFOW</c>, as <c>GetStartupInfoW</c> fills it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint StructureSize;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedData;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }
}
