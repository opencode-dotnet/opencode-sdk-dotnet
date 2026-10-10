using System.Globalization;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The descriptors a process holds open: this test process, so a launcher proof can show that none
/// of the pipes a start created is still open after the path ends, or a child, so a proof can show
/// what crossed into it. Both platforms read them in this process, Linux from
/// <c>/proc/&lt;pid&gt;/fd</c> and macOS through <c>libproc</c>, so a snapshot starts no process and
/// adds no pipe of its own to the process it lists.
/// </summary>
internal static class HostDescriptors
{
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>Reads every descriptor of this process that holds a pipe end.</summary>
    /// <returns>What each such descriptor holds.</returns>
    public static async Task<Dictionary<int, OpenDescriptor>> PipesAsync()
    {
        var all = await DescriptorsOfAsync(CurrentProcessId());
        return all.Where(static entry => entry.Value.IsPipe).ToDictionary(static entry => entry.Key, static entry => entry.Value);
    }

    /// <summary>Reads every open descriptor of a process.</summary>
    /// <param name="processId">The process; this one or one of the same user.</param>
    /// <returns>What each descriptor holds.</returns>
    public static Task<Dictionary<int, OpenDescriptor>> DescriptorsOfAsync(int processId) =>
        Task.FromResult(OperatingSystem.IsLinux() ? LinuxDescriptors(processId) : MacProcessDescriptors.Of(processId));

    /// <summary>Gets this process's pid on every target framework.</summary>
    /// <returns>The pid.</returns>
    public static int CurrentProcessId()
    {
#if NET
        return Environment.ProcessId;
#else
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return current.Id;
#endif
    }

    private static Dictionary<int, OpenDescriptor> LinuxDescriptors(int processId)
    {
        var descriptors = new Dictionary<int, OpenDescriptor>();
        foreach (var entry in FileSystem.Directory.EnumerateFileSystemEntries("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/fd"))
        {
            // A descriptor closed between the listing and the read holds nothing now, which the
            // probe reports as null.
            if (PosixProcessProbe.LinkTarget(entry) is { } target &&
                int.TryParse(FileSystem.Path.GetFileName(entry), NumberStyles.Integer, CultureInfo.InvariantCulture, out var descriptor))
            {
                descriptors[descriptor] = new OpenDescriptor(target, Peer: null, IsPipe: target.StartsWith("pipe:", StringComparison.Ordinal));
            }
        }

        return descriptors;
    }
}
