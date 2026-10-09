using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows.Abstractions;

/// <summary>
/// The job seam: the one job per process that ends every server the launcher started when the
/// process that owns them ends, however it ends.
/// </summary>
internal interface IProcessJob
{
    /// <summary>Assigns a server, created suspended, to the job; the job is created on first use.</summary>
    /// <param name="process">The server's process handle.</param>
    /// <returns><see cref="JobAssignment.Assigned"/>, or <see cref="JobAssignment.Refused"/> when the kernel refused with <c>ERROR_ACCESS_DENIED</c>.</returns>
    /// <exception cref="Win32Exception">The job could not be created, or the kernel refused the assignment for another reason.</exception>
    public JobAssignment Assign(SafeProcessHandle process);
}
