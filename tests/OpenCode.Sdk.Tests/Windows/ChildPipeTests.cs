using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Windows;

/// <summary>
/// A child's standard stream as libuv and .NET 11 make it: the parent's end overlapped, so a pending
/// read holds no thread, and the child's end synchronous, as a child's runtime expects; neither end
/// inheritable until the spawn makes a copy so. The witness of the mode is the handle's own I/O
/// mode, the question the runtime's <c>SafeFileHandle.IsAsync</c> asks; the bytes and the
/// end-of-stream must still cross.
/// </summary>
public sealed class ChildPipeTests
{
    /// <summary><c>FILE_SYNCHRONOUS_IO_ALERT | FILE_SYNCHRONOUS_IO_NONALERT</c>: either bit makes a handle synchronous.</summary>
    private const uint SynchronousIo = 0x30;

    /// <summary><c>FileModeInformation</c>, the class <c>NtQueryInformationFile</c> answers a handle's I/O mode for.</summary>
    private const int FileModeInformation = 16;

    private static readonly RealFileSystem FileSystem = new();

    [Test]
    public async Task ForChildOutput_Should_Read_Overlapped_And_Hand_The_Child_A_Synchronous_Write_End()
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the runtime's socket-backed anonymous pipe, no Windows handle mode to check");
            return;
        }

        using var pipe = ChildPipe.ForChildOutput();
        var readEnd = (System.IO.Pipes.PipeStream)pipe.Parent!;
        var writeEnd = pipe.Child!;

        await Assert.That(IoMode(readEnd.SafePipeHandle) & SynchronousIo).IsEqualTo(0u);
        await Assert.That(IoMode(writeEnd) & SynchronousIo).IsNotEqualTo(0u);
        await Assert.That(WindowsProcessProbe.IsInheritable(readEnd.SafePipeHandle)).IsFalse();
        await Assert.That(WindowsProcessProbe.IsInheritable(writeEnd)).IsFalse();

        var sent = Encoding.UTF8.GetBytes("server stderr\n");
        using (var writer = FileSystem.FileStream.New(writeEnd, FileAccess.Write, bufferSize: 1, isAsync: false))
        {
#if NET
            await writer.WriteAsync(sent.AsMemory());
#else
            await writer.WriteAsync(sent, 0, sent.Length);
#endif
        }

        using var received = new MemoryStream();
        await readEnd.CopyToAsync(received);

        await Assert.That(Encoding.UTF8.GetString(received.ToArray())).IsEqualTo("server stderr\n");
        BranchReport.Print("Windows — read end mode 0x" + IoMode(readEnd.SafePipeHandle).ToString("X", CultureInfo.InvariantCulture) + " (overlapped), write end synchronous, neither inheritable, bytes and end-of-stream crossed");
    }

    [Test]
    public async Task ForChildInput_Should_Give_The_Child_A_Synchronous_Read_End_That_Ends_When_The_Parent_Closes()
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the runtime's socket-backed anonymous pipe, no Windows handle mode to check");
            return;
        }

        using var pipe = ChildPipe.ForChildInput();
        var readEnd = pipe.Child!;

        await Assert.That(IoMode(readEnd) & SynchronousIo).IsNotEqualTo(0u);
        await Assert.That(WindowsProcessProbe.IsInheritable(readEnd)).IsFalse();

        // The lease: the parent never writes, and closing its end is the child's end-of-stream.
        await pipe.TakeParent()!.DisposeAsync();
        using var reader = FileSystem.FileStream.New(readEnd, FileAccess.Read, bufferSize: 1, isAsync: false);
        var buffer = new byte[1];
#if NET
        var read = await reader.ReadAsync(buffer.AsMemory());
#else
        var read = await reader.ReadAsync(buffer, 0, buffer.Length);
#endif

        await Assert.That(read).IsEqualTo(0);
        BranchReport.Print("Windows — the child's read end is synchronous and reads end-of-stream once the parent's end closed");
    }

    /// <summary>The handle's I/O mode, the question the runtime's <c>SafeFileHandle.IsAsync</c> asks.</summary>
    private static uint IoMode(SafeHandle handle)
    {
        var status = QueryInformationFile(handle, out _, out var mode, sizeof(uint), FileModeInformation);
        if (status != 0)
        {
            throw new Win32Exception("NtQueryInformationFile failed with NTSTATUS 0x" + status.ToString("X8", CultureInfo.InvariantCulture));
        }

        return mode;
    }

    [DllImport("ntdll", EntryPoint = "NtQueryInformationFile")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int QueryInformationFile(SafeHandle handle, out IoStatusBlock status, out uint mode, uint length, int informationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }
}
