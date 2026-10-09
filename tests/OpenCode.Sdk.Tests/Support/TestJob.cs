using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenCode.Sdk.Internal.Windows;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// A job of the test's own, for a launcher host that must itself run in a job, as a host started by
/// a CI runner, a service manager, or a Node client does. It always ends its members when the test
/// closes it, so nothing the host started outlives the test.
/// </summary>
internal sealed class TestJob : IDisposable
{
    /// <summary><c>JobObjectBasicUIRestrictions</c>.</summary>
    private const int BasicUiRestrictionsClass = 4;

    /// <summary><c>JOB_OBJECT_UILIMIT_HANDLES</c>: members cannot use user handles of processes outside the job.</summary>
    private const uint UiLimitHandles = 0x00000001;

    private TestJob(SafeJobHandle handle) => Handle = handle;

    /// <summary>Gets the job.</summary>
    public SafeJobHandle Handle { get; }

    /// <summary>Creates a kill-on-close job with these limits on top.</summary>
    /// <param name="limits">The extra <c>JOB_OBJECT_LIMIT_*</c> flags.</param>
    /// <param name="restrictUserInterface">Whether the job also carries a user-interface restriction.</param>
    /// <returns>The job.</returns>
    public static TestJob Create(uint limits, bool restrictUserInterface = false)
    {
        var handle = new JobObjects().Create(WindowsInterop.JobLimitKillOnJobClose | limits);
        if (restrictUserInterface)
        {
            var restrictions = UiLimitHandles;
            if (!SetUiRestrictions(handle, BasicUiRestrictionsClass, ref restrictions, sizeof(uint)))
            {
                var failure = new Win32Exception(Marshal.GetLastWin32Error());
                handle.Dispose();
                throw failure;
            }
        }

        return new TestJob(handle);
    }

    /// <summary>Closes the job, which ends every process still in it.</summary>
    public void Dispose() => Handle.Dispose();

    [DllImport("kernel32", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetUiRestrictions(SafeJobHandle job, int informationClass, ref uint restrictions, uint length);
}
