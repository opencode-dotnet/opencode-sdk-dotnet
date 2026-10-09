using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// One standard stream of a child as a pipe, made the way libuv makes a child's stdio and .NET 11's
/// <c>Process</c> makes its output pipes: a local named pipe whose parent end is overlapped, so a
/// pending read waits on the I/O completion port and holds no thread, and whose child end is
/// synchronous, as a child's runtime expects its standard handles. Each end carries one direction:
/// the child's input end reads and an output end writes, and each carries the attribute right of
/// the other direction and <c>WRITE_DAC</c>, libuv's own masks, so the child's runtime can query
/// and set the state of its stdio. The name is fresh, under <c>\\.\pipe\LOCAL\</c>, the one
/// namespace an AppContainer host can create pipes in; the flags refuse a second instance and
/// remote clients, so the child end opened here is the only client. Both ends are created
/// non-inheritable: the spawn makes a copy of the child end inheritable only while it creates the
/// child.
/// </summary>
internal static class ChildPipe
{
    /// <summary>libuv's buffer size in both directions: a burst of server output waits in the pipe without blocking the server.</summary>
    private const uint BufferSize = 65536;

    /// <summary>Creates the pipe for a stream the child reads: its standard input.</summary>
    /// <returns>The parent's write end and the child's read end.</returns>
    /// <exception cref="Win32Exception">The pipe or its child end could not be created.</exception>
    public static ChildStreamEnd ForChildInput() =>
        Create(
            WindowsInterop.PipeAccessOutbound,
            WindowsInterop.GenericRead | WindowsInterop.FileWriteAttributes | WindowsInterop.WriteDac,
            PipeDirection.Out);

    /// <summary>Creates the pipe for a stream the child writes: its standard output or error.</summary>
    /// <returns>The parent's read end and the child's write end.</returns>
    /// <exception cref="Win32Exception">The pipe or its child end could not be created.</exception>
    public static ChildStreamEnd ForChildOutput() =>
        Create(
            WindowsInterop.PipeAccessInbound,
            WindowsInterop.GenericWrite | WindowsInterop.FileReadAttributes | WindowsInterop.WriteDac,
            PipeDirection.In);

    private static ChildStreamEnd Create(uint parentAccess, uint childAccess, PipeDirection parentDirection)
    {
        var name = @"\\.\pipe\LOCAL\opencode-sdk-" + Guid.NewGuid().ToString("N");
        SafePipeHandle? parentEnd = null;
        SafeFileHandle? childEnd = null;
        try
        {
            parentEnd = WindowsInterop.CreateNamedPipe(
                name,
                parentAccess | WindowsInterop.FirstPipeInstance | WindowsInterop.Overlapped,
                WindowsInterop.LocalBytePipe,
                maxInstances: 1,
                BufferSize,
                BufferSize,
                defaultTimeout: 0,
                securityAttributes: IntPtr.Zero);
            if (parentEnd.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            childEnd = WindowsInterop.CreateFile(
                name, childAccess, 0, IntPtr.Zero, WindowsInterop.OpenExisting, 0, IntPtr.Zero);
            if (childEnd.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var stream = new NamedPipeServerStream(parentDirection, isAsync: true, isConnected: true, parentEnd);
            parentEnd = null;
            var end = new ChildStreamEnd(stream, childEnd);
            childEnd = null;
            return end;
        }
        finally
        {
            childEnd?.Dispose();
            parentEnd?.Dispose();
        }
    }
}
