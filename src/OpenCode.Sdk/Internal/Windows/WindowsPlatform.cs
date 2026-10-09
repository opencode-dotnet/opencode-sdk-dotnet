#if !NET
using System.Runtime.InteropServices;
#endif

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// Whether the process runs on Windows, asked the same way on every target framework: the
/// launcher, the background-service door, and their seams each pick their platform arm by it.
/// </summary>
internal static class WindowsPlatform
{
    /// <summary>Gets a value indicating whether the process runs on Windows.</summary>
    public static bool IsWindows =>
#if NET
        OperatingSystem.IsWindows();
#else
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif
}
