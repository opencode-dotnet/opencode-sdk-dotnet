using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>The shipped <see cref="IJobObjects"/>: the kernel's own calls.</summary>
internal sealed class JobObjects : IJobObjects
{
    /// <inheritdoc />
    public SafeJobHandle Create(uint limitFlags)
    {
        // No security attributes: the handle is not inheritable, so no child of the host, ours or
        // anyone's, can hold the job open past the host's own end.
        var job = WindowsInterop.CreateJobObject(IntPtr.Zero, name: null);
        if (job.IsInvalid)
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw failure;
        }

        var limits = new WindowsInterop.JobExtendedLimitInformation
        {
            BasicLimitInformation = new WindowsInterop.JobBasicLimitInformation { LimitFlags = limitFlags },
        };
        if (!WindowsInterop.SetInformationJobObject(
                job,
                WindowsInterop.JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<WindowsInterop.JobExtendedLimitInformation>()))
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw failure;
        }

        return job;
    }

    /// <inheritdoc />
    public int Assign(SafeJobHandle job, SafeProcessHandle process) =>
        WindowsInterop.AssignProcessToJobObject(job, process) ? 0 : Marshal.GetLastWin32Error();
}
