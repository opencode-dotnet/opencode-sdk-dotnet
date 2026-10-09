using System.Globalization;
using System.Runtime.InteropServices;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Windows;

/// <summary>
/// The kernel reads these structures by their C layout, which differs by bitness: a wrong size makes
/// <c>SetInformationJobObject</c> fail with <c>ERROR_BAD_LENGTH</c>, and a wrong offset hands the
/// kernel other flags than the launcher meant. The sizes are the Windows SDK's on the running
/// process's bitness: 64-bit (x64, arm64) and 32-bit (x86, the default of a .NET Framework
/// executable that prefers 32-bit). Layout is a property of the build, not of the operating system,
/// so these run on every platform.
/// </summary>
public sealed class WindowsInteropLayoutTests
{
    private static bool Is64Bit => IntPtr.Size == 8;

    [Test]
    public async Task JobExtendedLimitInformation_Should_Have_The_Windows_Sdk_Layout()
    {
        var size = Marshal.SizeOf<WindowsInterop.JobExtendedLimitInformation>();
        var ioInfo = Marshal.OffsetOf<WindowsInterop.JobExtendedLimitInformation>(nameof(WindowsInterop.JobExtendedLimitInformation.IoInfo)).ToInt32();
        var processMemoryLimit = Marshal.OffsetOf<WindowsInterop.JobExtendedLimitInformation>(nameof(WindowsInterop.JobExtendedLimitInformation.ProcessMemoryLimit)).ToInt32();

        await Assert.That(size).IsEqualTo(Is64Bit ? 144 : 112);
        await Assert.That(ioInfo).IsEqualTo(Is64Bit ? 64 : 48);
        await Assert.That(processMemoryLimit).IsEqualTo(Is64Bit ? 112 : 96);
        BranchReport.Print((Is64Bit ? "64-bit" : "32-bit") + " — JOBOBJECT_EXTENDED_LIMIT_INFORMATION is " + size.ToString(CultureInfo.InvariantCulture) + " bytes");
    }

    [Test]
    public async Task JobBasicLimitInformation_Should_Have_The_Windows_Sdk_Layout()
    {
        var size = Marshal.SizeOf<WindowsInterop.JobBasicLimitInformation>();
        var limitFlags = Marshal.OffsetOf<WindowsInterop.JobBasicLimitInformation>(nameof(WindowsInterop.JobBasicLimitInformation.LimitFlags)).ToInt32();
        var minimumWorkingSet = Marshal.OffsetOf<WindowsInterop.JobBasicLimitInformation>(nameof(WindowsInterop.JobBasicLimitInformation.MinimumWorkingSetSize)).ToInt32();
        var affinity = Marshal.OffsetOf<WindowsInterop.JobBasicLimitInformation>(nameof(WindowsInterop.JobBasicLimitInformation.Affinity)).ToInt32();

        await Assert.That(size).IsEqualTo(Is64Bit ? 64 : 48);
        await Assert.That(limitFlags).IsEqualTo(16);
        await Assert.That(minimumWorkingSet).IsEqualTo(Is64Bit ? 24 : 20);
        await Assert.That(affinity).IsEqualTo(Is64Bit ? 48 : 32);
    }

    [Test]
    public async Task IoCounters_Should_Have_The_Windows_Sdk_Layout() =>
        await Assert.That(Marshal.SizeOf<WindowsInterop.IoCounters>()).IsEqualTo(48);

    [Test]
    public async Task StartupInfo_Should_Have_The_Windows_Sdk_Layout()
    {
        var startupInfo = Marshal.SizeOf<WindowsInterop.StartupInfo>();
        var startupInfoEx = Marshal.SizeOf<WindowsInterop.StartupInfoEx>();
        var standardInput = Marshal.OffsetOf<WindowsInterop.StartupInfo>(nameof(WindowsInterop.StartupInfo.StandardInput)).ToInt32();

        await Assert.That(startupInfo).IsEqualTo(Is64Bit ? 104 : 68);
        await Assert.That(startupInfoEx).IsEqualTo(Is64Bit ? 112 : 72);
        await Assert.That(standardInput).IsEqualTo(Is64Bit ? 80 : 56);
    }

    [Test]
    public async Task ProcessInformation_Should_Have_The_Windows_Sdk_Layout() =>
        await Assert.That(Marshal.SizeOf<WindowsInterop.ProcessInformation>()).IsEqualTo(Is64Bit ? 24 : 16);
}
