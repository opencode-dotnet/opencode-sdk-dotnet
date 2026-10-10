using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The descriptors a macOS process holds, read in this process through <c>libproc</c>
/// (<c>proc_pidinfo</c> and <c>proc_pidfdinfo</c>), as Linux reads <c>/proc</c>. No child process
/// lists them: a listing tool started for a snapshot would hold its own exec-wait pipe, both ends in
/// this process, until its start returns, and a tool that scans this process before then would
/// report that pipe as new. A pipe end is named by its kernel address and its peer's, as
/// <c>lsof</c> names it; a file by its device, inode and path; a socket by its kernel address; so
/// two descriptors that share one open file, in this process or another, carry the same name.
/// Anything else is named by its type alone.
/// </summary>
internal static class MacProcessDescriptors
{
    private const int ProcPidListFds = 1;
    private const int ProcPidFdVnodePathInfo = 2;
    private const int ProcPidFdSocketInfo = 3;
    private const int ProcPidFdPipeInfo = 6;
    private const uint FdTypeVnode = 1;
    private const uint FdTypeSocket = 2;
    private const uint FdTypePipe = 6;
    private const int BadDescriptor = 9;

    /// <summary><c>struct proc_fdinfo</c>: an <c>int32</c> descriptor and a <c>uint32</c> type.</summary>
    private const int FdInfoSize = 8;

    /// <summary><c>struct proc_fileinfo</c>, which every per-descriptor record starts with.</summary>
    private const int FileInfoSize = 24;

    /// <summary><c>struct vinfo_stat</c>, which a pipe's and a vnode's record carry first.</summary>
    private const int StatSize = 136;

    /// <summary><c>struct pipe_fdinfo</c>: file info, then stat, both handles, status and a spare word.</summary>
    private const int PipeInfoSize = FileInfoSize + StatSize + 8 + 8 + 4 + 4;

    private const int PipeHandleOffset = FileInfoSize + StatSize;
    private const int PeerHandleOffset = PipeHandleOffset + 8;

    /// <summary><c>struct vnode_fdinfowithpath</c>: file info, the vnode info (stat, type, pad, fsid), then the path.</summary>
    private const int VnodeInfoSize = StatSize + 4 + 4 + 8;

    private const int PathOffset = FileInfoSize + VnodeInfoSize;
    private const int MaxPathLength = 1024;
    private const int VnodePathInfoSize = PathOffset + MaxPathLength;

    /// <summary>
    /// <c>struct socket_fdinfo</c>: file info, then <c>socket_info</c>, which starts with a stat and
    /// the socket's kernel address <c>soi_so</c>. The record is far larger; the buffer is ample, and
    /// only the address is read.
    /// </summary>
    private const int SocketHandleOffset = FileInfoSize + StatSize;

    private const int MinimumSocketInfoSize = SocketHandleOffset + 8;
    private const int SocketInfoCapacity = 4096;

    /// <summary><c>vst_dev</c> (<c>uint32</c>) and <c>vst_ino</c> (<c>uint64</c>), at the start of the stat.</summary>
    private const int DeviceOffset = FileInfoSize;
    private const int InodeOffset = FileInfoSize + 8;

    /// <summary>Room for descriptors the process opens between the size probe and the listing.</summary>
    private const int ListingHeadroom = 64 * FdInfoSize;

    /// <summary>Reads every open descriptor of a process of the same user.</summary>
    /// <param name="processId">The process.</param>
    /// <returns>What each descriptor holds.</returns>
    public static Dictionary<int, OpenDescriptor> Of(int processId)
    {
        var needed = ListFileDescriptors(processId, ProcPidListFds, 0, null, 0);
        if (needed <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "proc_pidinfo could not size the descriptor list of " + processId.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var listing = new byte[needed + ListingHeadroom];
        var listed = ListFileDescriptors(processId, ProcPidListFds, 0, listing, listing.Length);
        if (listed <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "proc_pidinfo could not list the descriptors of " + processId.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var descriptors = new Dictionary<int, OpenDescriptor>();
        for (var at = 0; at + FdInfoSize <= listed; at += FdInfoSize)
        {
            var descriptor = BinaryPrimitives.ReadInt32LittleEndian(listing.AsSpan(at));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(listing.AsSpan(at + 4));

            // A descriptor closed between the listing and the read holds nothing now, as on Linux.
            if (Describe(processId, descriptor, type) is { } described)
            {
                descriptors[descriptor] = described;
            }
        }

        return descriptors;
    }

    private static OpenDescriptor? Describe(int processId, int descriptor, uint type)
    {
        switch (type)
        {
            case FdTypePipe:
                return Read(processId, descriptor, ProcPidFdPipeInfo, PipeInfoSize, exactSize: true) is { } pipe
                    ? new OpenDescriptor(Address(pipe, PipeHandleOffset), Peer: Address(pipe, PeerHandleOffset), IsPipe: true)
                    : null;

            case FdTypeVnode:
                if (Read(processId, descriptor, ProcPidFdVnodePathInfo, VnodePathInfoSize, exactSize: true) is not { } file)
                {
                    return null;
                }

                var device = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(DeviceOffset));
                var inode = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(InodeOffset));
                return new OpenDescriptor(
                    "VNODE:" + device.ToString(CultureInfo.InvariantCulture) + ":" + inode.ToString(CultureInfo.InvariantCulture) + ":" + PathAt(file, PathOffset),
                    Peer: null,
                    IsPipe: false);

            case FdTypeSocket:
                return Read(processId, descriptor, ProcPidFdSocketInfo, SocketInfoCapacity, exactSize: false) is { } socket
                    ? new OpenDescriptor("SOCKET:" + Address(socket, SocketHandleOffset), Peer: null, IsPipe: false)
                    : null;

            default:
                return new OpenDescriptor("TYPE" + type.ToString(CultureInfo.InvariantCulture), Peer: null, IsPipe: false);
        }
    }

    /// <summary>
    /// One <c>proc_pidfdinfo</c> record. The kernel answers the exact size of its record, or 0 with
    /// <c>errno</c>: <c>EBADF</c> for a descriptor closed (or reused for another type) since the
    /// listing, which holds nothing now; anything else, such as <c>ENOMEM</c> when the kernel's
    /// record outgrew the buffer, is a reader fault and throws rather than reading as closed.
    /// </summary>
    private static byte[]? Read(int processId, int descriptor, int flavor, int size, bool exactSize)
    {
        var info = new byte[size];
        var read = FileDescriptorInfo(processId, descriptor, flavor, info, info.Length);
        if (read == 0)
        {
            var error = Marshal.GetLastWin32Error();
            return error == BadDescriptor
                ? null
                : throw new Win32Exception(error, "proc_pidfdinfo flavor " + flavor.ToString(CultureInfo.InvariantCulture) + " failed for descriptor " + descriptor.ToString(CultureInfo.InvariantCulture) + " of " + processId.ToString(CultureInfo.InvariantCulture) + " with errno " + error.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var expected = exactSize ? read == size : read >= MinimumSocketInfoSize;
        return expected
            ? info
            : throw new InvalidOperationException("proc_pidfdinfo flavor " + flavor.ToString(CultureInfo.InvariantCulture) + " returned " + read.ToString(CultureInfo.InvariantCulture) + " bytes; the layout this reader assumes is " + size.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private static string Address(byte[] info, int offset) =>
        "0x" + BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(offset)).ToString("x", CultureInfo.InvariantCulture);

    private static string PathAt(byte[] info, int offset)
    {
        var length = Array.IndexOf(info, (byte)0, offset, MaxPathLength) is var end and >= 0 ? end - offset : MaxPathLength;
        return Encoding.UTF8.GetString(info, offset, length);
    }

    [DllImport("libc", EntryPoint = "proc_pidinfo", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ListFileDescriptors(int processId, int flavor, ulong argument, byte[]? buffer, int bufferSize);

    [DllImport("libc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int FileDescriptorInfo(int processId, int descriptor, int flavor, byte[] buffer, int bufferSize);
}
