using System.Diagnostics;
using System.Globalization;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The descriptors a process holds open: this test process, so a launcher proof can show that none
/// of the pipes a start created is still open after the path ends, or a child, so a proof can show
/// what crossed into it. Linux reads <c>/proc/&lt;pid&gt;/fd</c>; macOS has no <c>/proc</c>, so
/// <c>lsof</c> lists them there, into a file rather than a pipe, so the listing adds no pipe of its
/// own to the process it lists.
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
    public static async Task<Dictionary<int, OpenDescriptor>> DescriptorsOfAsync(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxDescriptors(processId);
        }

        using var runRoot = new TestRunRoot(FileSystem);
        var listingFile = FileSystem.Path.Combine(runRoot.Path, "lsof");
        using var lsof = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"/usr/sbin/lsof -n -P -a -p " + processId.ToString(CultureInfo.InvariantCulture) + " -d 0-65535 -F ftdn > '" + listingFile + "'\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("lsof did not start.");
        await lsof.WaitForExitAsync();
#if NET
        var listing = await FileSystem.File.ReadAllTextAsync(listingFile);
#else
        var listing = FileSystem.File.ReadAllText(listingFile);
#endif
        return ParseLsof(listing);
    }

    /// <summary>Gets this process's pid on every target framework.</summary>
    /// <returns>The pid.</returns>
    public static int CurrentProcessId()
    {
#if NET
        return Environment.ProcessId;
#else
        using var current = Process.GetCurrentProcess();
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

    /// <summary>
    /// <c>lsof -F ftdn</c>: one <c>f</c> line per descriptor, then its type, its device (for a pipe
    /// end, the end's address), and its name (for a pipe end, <c>-&gt;0x</c> and the peer's address).
    /// </summary>
    private static Dictionary<int, OpenDescriptor> ParseLsof(string listing)
    {
        var descriptors = new Dictionary<int, OpenDescriptor>();
        int? descriptor = null;
        var type = string.Empty;
        var device = string.Empty;
        foreach (var line in listing.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case 'f':
                    descriptor = int.TryParse(line.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
                    type = string.Empty;
                    device = string.Empty;
                    if (descriptor is { } opened)
                    {
                        descriptors[opened] = new OpenDescriptor("?", Peer: null, IsPipe: false);
                    }

                    break;
                case 't':
                    type = line[1..];
                    break;
                case 'd':
                    device = line[1..];
                    break;
                case 'n' when descriptor is { } open:
                    var name = line[1..];
                    descriptors[open] = string.Equals(type, "PIPE", StringComparison.Ordinal)
                        ? new OpenDescriptor(device, PeerOf(name), IsPipe: true)
                        : new OpenDescriptor(type + ":" + device + ":" + name, Peer: null, IsPipe: false);
                    break;
                default:
                    break;
            }
        }

        return descriptors;
    }

    /// <summary>A pipe end's <c>lsof</c> name is <c>-&gt;0x</c> and its peer's address.</summary>
    private static string? PeerOf(string name) =>
        name.StartsWith("->", StringComparison.Ordinal) ? name[2..].Split(' ')[0] : null;
}
