using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Runtime.InteropServices;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// One running process, marked by the start the operating system recorded for it in a form every
/// process reads alike — what a test needs to recognize a process another process recorded. On
/// Windows and macOS that is <see cref="Process.StartTime"/>, which the kernel keeps as an absolute
/// time. On Linux it is the clock-tick count after boot in <c>/proc/&lt;pid&gt;/stat</c> (field 22):
/// .NET's <see cref="Process.StartTime"/> adds that count to a boot time each process derives once
/// from the wall clock, so two processes started either side of a clock step disagree about the
/// same process — readings under WSL2 were measured tens of seconds apart — and the sum identifies
/// a process only inside the process that read it.
/// </summary>
/// <param name="ProcessId">The pid.</param>
/// <param name="Start">The platform's start marker: .NET ticks on Windows and macOS, clock ticks after boot on Linux.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct ProcessMark(int ProcessId, long Start)
{
    /// <summary>
    /// Marks the process running under a pid right now: null when none runs under it, when it is a
    /// Linux zombie — exited, waiting only to be reaped — or when its start cannot be read.
    /// </summary>
    /// <param name="fileSystem">The filesystem <c>/proc</c> is read through.</param>
    /// <param name="processId">The pid.</param>
    /// <returns>The mark, or null.</returns>
    public static ProcessMark? TryRead(IFileSystem fileSystem, int processId)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return TryReadLinux(fileSystem, processId);
            }

            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : new ProcessMark(processId, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // No process under the pid, or one whose start the platform will not tell: either way
            // there is nothing this mark could later be matched against.
            return null;
        }
    }

    /// <summary>Whether the process this mark names still runs under its pid.</summary>
    /// <param name="fileSystem">The filesystem <c>/proc</c> is read through.</param>
    /// <returns>True when the pid still names this very process.</returns>
    public bool IsRunning(IFileSystem fileSystem) => TryRead(fileSystem, ProcessId) == this;

    public override string ToString() =>
        ProcessId.ToString(CultureInfo.InvariantCulture) + " " + Start.ToString(CultureInfo.InvariantCulture);

    private static ProcessMark? TryReadLinux(IFileSystem fileSystem, int processId)
    {
        // The command name may itself hold ')' or spaces, so the fields are read after the last ')':
        // state (field 3) first, start time (field 22) nineteen fields on. The ')' is scanned for
        // by hand, the way the stop door's zombie check does: LastIndexOf's char and string
        // overloads trade MA0001 against CA1865/MA0089.
        var stat = fileSystem.File.ReadAllText("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/stat");
        var close = stat.Length - 1;
        while (close >= 0 && stat[close] != ')')
        {
            close--;
        }

        var fields = stat[(close + 2)..].Split(' ');
        return fields is [not "Z", ..] && fields.Length > 19
            && long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                ? new ProcessMark(processId, start)
                : null;
    }
}
