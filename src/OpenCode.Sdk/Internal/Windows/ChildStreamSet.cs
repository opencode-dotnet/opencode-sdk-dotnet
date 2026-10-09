using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The three standard streams of a child being created, each opened where its route leads: a
/// <see cref="ChildPipe"/>, NUL, or the host's own handle of the same number. Disposal closes the
/// child's side of each, and the parent's side of each that was not handed over.
/// </summary>
internal sealed class ChildStreamSet : IDisposable
{
    private ChildStreamSet()
    {
    }

    /// <summary>Gets the standard input's two sides.</summary>
    public ChildStreamEnd? Input { get; private set; }

    /// <summary>Gets the standard output's two sides.</summary>
    public ChildStreamEnd? Output { get; private set; }

    /// <summary>Gets the standard error's two sides.</summary>
    public ChildStreamEnd? Error { get; private set; }

    /// <summary>Opens each stream where its route leads; nothing when the child receives no handle at all.</summary>
    /// <param name="routes">Where the streams lead; null opens none.</param>
    /// <returns>The opened streams.</returns>
    /// <exception cref="Win32Exception">A pipe or NUL could not be opened; nothing opened here is left open.</exception>
    public static ChildStreamSet Open(WindowsStandardStreams? routes)
    {
        var set = new ChildStreamSet();
        if (routes is null)
        {
            return set;
        }

        try
        {
            set.Input = OpenOne(routes.Input, WindowsInterop.StandardInputHandle);
            set.Output = OpenOne(routes.Output, WindowsInterop.StandardOutputHandle);
            set.Error = OpenOne(routes.Error, WindowsInterop.StandardErrorHandle);
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    /// <summary>Closes the child's sides here, and the parent's sides not handed over.</summary>
    public void Dispose()
    {
        Input?.Dispose();
        Output?.Dispose();
        Error?.Dispose();
    }

    private static ChildStreamEnd OpenOne(ChildStreamRoute route, int standardHandle) => route switch
    {
        ChildStreamRoute.Pipe when standardHandle == WindowsInterop.StandardInputHandle => ChildPipe.ForChildInput(),
        ChildStreamRoute.Pipe => ChildPipe.ForChildOutput(),
        ChildStreamRoute.Null => new ChildStreamEnd(null, OpenNul()),
        ChildStreamRoute.Inherit => new ChildStreamEnd(null, HostHandle(standardHandle)),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown standard stream route."),
    };

    /// <summary>Opens NUL for one stream, non-inheritable like every other handle the spawn creates.</summary>
    private static SafeFileHandle OpenNul()
    {
        var nul = WindowsInterop.CreateFile(
            "NUL",
            WindowsInterop.GenericRead | WindowsInterop.GenericWrite,
            WindowsInterop.ShareReadWrite,
            IntPtr.Zero,
            WindowsInterop.OpenExisting,
            0,
            IntPtr.Zero);
        if (nul.IsInvalid)
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            nul.Dispose();
            throw failure;
        }

        return nul;
    }

    /// <summary>
    /// The host's own handle of the same number, wrapped without ownership; none when the host has
    /// none (a GUI host or a service), as libuv passes none.
    /// </summary>
    private static SafeFileHandle? HostHandle(int standardHandle)
    {
        var handle = WindowsInterop.GetStdHandle(standardHandle);
        return handle == IntPtr.Zero || handle == WindowsInterop.InvalidHandleValue
            ? null
            : new SafeFileHandle(handle, ownsHandle: false);
    }
}
