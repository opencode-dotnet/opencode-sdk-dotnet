using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows.Abstractions;

/// <summary>
/// The kernel's job-object calls the launcher's job makes: create a job with limits, and assign a
/// process to it. A test replaces them to drive the job's own decisions (a failed creation, a
/// refused or invalid assignment) without a kernel that fails on cue.
/// </summary>
internal interface IJobObjects
{
    /// <summary>Creates an unnamed, non-inheritable job with exactly these limit flags.</summary>
    /// <param name="limitFlags">The <c>JOB_OBJECT_LIMIT_*</c> flags.</param>
    /// <returns>The job.</returns>
    /// <exception cref="Win32Exception">The job could not be created or limited; nothing is left open.</exception>
    public SafeJobHandle Create(uint limitFlags);

    /// <summary>Assigns a process to a job.</summary>
    /// <param name="job">The job.</param>
    /// <param name="process">The process.</param>
    /// <returns>Zero when the process was assigned; otherwise the Windows error code.</returns>
    public int Assign(SafeJobHandle job, SafeProcessHandle process);
}
