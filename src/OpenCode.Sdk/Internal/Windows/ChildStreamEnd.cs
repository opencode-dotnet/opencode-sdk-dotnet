using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The two sides of one standard stream while a child is being created: the parent's end, which
/// passes to the caller once the child exists, and the handle the child receives, which the parent
/// closes as soon as the child holds its own copy. An end the host passes on as it is (its own
/// standard handle) is wrapped without ownership, so disposing it never closes the host's handle.
/// </summary>
internal sealed class ChildStreamEnd : IDisposable
{
    /// <summary>Initializes the two sides.</summary>
    /// <param name="parent">The parent's end, or null when the stream is no pipe.</param>
    /// <param name="child">The handle the child receives, or null when it receives none.</param>
    public ChildStreamEnd(Stream? parent, SafeFileHandle? child)
    {
        Parent = parent;
        Child = child;
    }

    /// <summary>Gets the parent's end, until <see cref="TakeParent"/> hands it over.</summary>
    public Stream? Parent { get; private set; }

    /// <summary>Gets the handle the child receives.</summary>
    public SafeFileHandle? Child { get; }

    /// <summary>Hands the parent's end to the caller, which owns it from here.</summary>
    /// <returns>The parent's end, or null when the stream is no pipe.</returns>
    public Stream? TakeParent()
    {
        var parent = Parent;
        Parent = null;
        return parent;
    }

    /// <summary>Closes the child's handle here, and the parent's end unless it was handed over.</summary>
    public void Dispose()
    {
        Child?.Dispose();
        Parent?.Dispose();
    }
}
