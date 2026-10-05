#if !NET
using System.Runtime.InteropServices;
#endif

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// The two POSIX kernels the SDK binds by number. Their spawn flags, signal numbers, and structure
/// layouts differ, so every caller that hands one of those to the C library asks which kernel runs,
/// on every target framework.
/// </summary>
internal static class PosixPlatform
{
    /// <summary>Gets a value indicating whether the process runs on Linux.</summary>
    public static bool IsLinux =>
#if NET
        OperatingSystem.IsLinux();
#else
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
#endif

    /// <summary>Gets a value indicating whether the process runs on macOS.</summary>
    public static bool IsMacOS =>
#if NET
        OperatingSystem.IsMacOS();
#else
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#endif
}
