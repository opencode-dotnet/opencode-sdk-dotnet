using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
#if !NET
using System.Runtime.InteropServices;
#endif
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.Posix;
using static OpenCode.Sdk.Internal.BackgroundService.ProcessControl.BackgroundServiceInterop;
using static OpenCode.Sdk.Internal.Posix.PosixInterop;

namespace OpenCode.Sdk.Internal.BackgroundService.ProcessControl;

/// <summary>
/// The shipped <see cref="IServiceProcessControl"/> on the platform's own primitives (ADR-0026).
/// Identity is the pid with the start time the operating system recorded for it
/// (<c>GetProcessTimes</c>, <c>/proc/&lt;pid&gt;/stat</c>, and <c>proc_pidinfo</c> behind
/// <see cref="Process.StartTime"/>), the token Aspire and psutil use, because a pid alone is reused
/// and <see cref="Process.Kill()"/> verifies nothing about a process this one did not start. On Unix
/// the rungs are <c>SIGTERM</c> and <c>SIGKILL</c> through <c>kill(2)</c>: .NET's public surface
/// sends only <c>SIGKILL</c>, and both the pinned client's runtime and .NET's own <c>Kill</c> reach
/// the same call one layer down. On Windows another process cannot be signalled, so both rungs are
/// <see cref="Process.Kill()"/> (<c>TerminateProcess</c>), which is what libuv does with a
/// <c>SIGTERM</c> there.
/// </summary>
internal sealed class ServiceProcessControl : IServiceProcessControl
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;

    public ProcessIdentity? TrySnapshot(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var identity = new ProcessIdentity(processId, process.StartTime.ToUniversalTime());
            return IsLinuxZombie(processId) ? null : identity;
        }
        catch (ArgumentException)
        {
            // No process runs under the pid.
            return null;
        }
        catch (InvalidOperationException)
        {
            // It exited between the lookup and the read.
            return null;
        }
        catch (Win32Exception)
        {
            // The platform refused the read (another user's process, a vanished /proc entry): the
            // pinned client reads a refused kill(pid, 0) the same way, as a process that is gone.
            return null;
        }
    }

    public bool TrySignal(ProcessIdentity target, ProcessSignal signal)
    {
        if (TrySnapshot(target.ProcessId) != target)
        {
            return false;
        }

        return IsWindows ? TryTerminateProcess(target.ProcessId) : TrySendSignal(target.ProcessId, signal);
    }

    private static bool TryTerminateProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A zombie serves nothing: it has exited and waits only for its parent to reap it, which is
    /// not this call's to do when the parent is another process. Linux keeps a zombie's
    /// <c>/proc/&lt;pid&gt;/stat</c> readable with its start time unchanged, so the identity alone
    /// would read it as running; the state field after the parenthesized command name says
    /// <c>Z</c>. macOS refuses a zombie's start time already, which reads as gone.
    /// </summary>
    private static bool IsLinuxZombie(int processId)
    {
        if (!IsLinux)
        {
            return false;
        }

        try
        {
            // The command name may itself hold ')' or spaces, so the state is found after the last
            // ')' — scanned by hand, since the char overload of LastIndexOf and its string twin trade
            // MA0001 against CA1865/MA0089 (the ServiceContenderSpawner.ContainsNul precedent).
            var stat = File.ReadAllText("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/stat");
            for (var index = stat.Length - 1; index >= 0; index--)
            {
                if (stat[index] == ')')
                {
                    return index + 2 < stat.Length && stat[index + 2] == 'Z';
                }
            }

            return false;
        }
        catch (IOException)
        {
            // The entry vanished between the lookup and this read: the process is gone either way,
            // and the identity read reports that on its next look.
            return false;
        }
    }

    private static bool TrySendSignal(int processId, ProcessSignal signal) =>
        Kill(processId, signal == ProcessSignal.Terminate ? Sigterm : Sigkill) == 0;

    private static bool IsLinux =>
#if NET
        OperatingSystem.IsLinux();
#else
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
#endif
}
