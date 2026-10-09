using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> attribute list a spawn hands <c>CreateProcessW</c>:
/// exactly the handles the child receives, and no other inheritable handle of the host. The list
/// and the handle values it points at live in unmanaged memory, because the attribute must stay
/// valid until the list is deleted; disposal deletes the list and frees both.
/// </summary>
internal sealed class InheritedHandleList : IDisposable
{
    private IntPtr _list;
    private IntPtr _values;

    private InheritedHandleList(IntPtr list, IntPtr values)
    {
        _list = list;
        _values = values;
    }

    /// <summary>Gets the initialized attribute list, for the startup info.</summary>
    public IntPtr Value => _list;

    /// <summary>Builds the list over the given handles, each inheritable and listed once.</summary>
    /// <param name="handles">The handles the child receives.</param>
    /// <returns>The list.</returns>
    /// <exception cref="Win32Exception">The list could not be sized, initialized, or filled.</exception>
    public static InheritedHandleList Create(IReadOnlyList<IntPtr> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);

        var size = IntPtr.Zero;
        _ = WindowsInterop.InitializeAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var values = Marshal.AllocHGlobal(handles.Count * IntPtr.Size);
        var list = IntPtr.Zero;
        var initialized = false;
        try
        {
            list = Marshal.AllocHGlobal(size);
            for (var index = 0; index < handles.Count; index++)
            {
                Marshal.WriteIntPtr(values, index * IntPtr.Size, handles[index]);
            }

            if (!WindowsInterop.InitializeAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            initialized = true;
            if (!WindowsInterop.UpdateAttribute(
                    list,
                    0,
                    WindowsInterop.HandleListAttribute,
                    values,
                    new IntPtr(handles.Count * IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var created = new InheritedHandleList(list, values);
            list = IntPtr.Zero;
            values = IntPtr.Zero;
            return created;
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                if (initialized)
                {
                    WindowsInterop.DeleteAttributeList(list);
                }

                Marshal.FreeHGlobal(list);
            }

            if (values != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(values);
            }
        }
    }

    /// <summary>Deletes the list and frees its memory.</summary>
    public void Dispose()
    {
        if (_list != IntPtr.Zero)
        {
            WindowsInterop.DeleteAttributeList(_list);
            Marshal.FreeHGlobal(_list);
            _list = IntPtr.Zero;
        }

        if (_values != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_values);
            _values = IntPtr.Zero;
        }
    }
}
