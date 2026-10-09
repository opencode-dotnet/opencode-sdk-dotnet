using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The shipped <see cref="IProcessJob"/>: one job per process, with exactly the four limits libuv
/// gives its own: kill on close, so the servers end when the job's last handle closes, which the
/// kernel does when the host ends, however it ends; breakaway allowed and silent breakaway, so no
/// child a server creates joins the job by inheritance, and a server's own detached descendants
/// outlive it; and death on an unhandled exception, so a crashing server ends without an
/// error-reporting dialog. A server built on libuv still places its other children here: its own
/// job nests under this one. The job is created on the first start and never closed by the SDK.
/// The host itself is not added. Two differences from libuv, both on failure paths: a job that
/// cannot be created fails the start instead of aborting the host, and that failure is not kept,
/// so a later start tries again. An assignment the kernel refuses with
/// <c>ERROR_INVALID_PARAMETER</c> (libuv's comment records the job of a Windows Store program's
/// first use failing every later assignment this way) is tried once more in a fresh job. When the
/// fresh job takes the process, it serves every later start and the old one stays held, because it
/// may hold a server; when it refuses too, it is closed, since it holds nothing, and the old one
/// stays in use, so a command the kernel always refuses neither displaces the job of the running
/// servers nor adds a held job per start.
/// </summary>
internal sealed class KillOnCloseJob : IProcessJob
{
    /// <summary>libuv's limits.</summary>
    public const uint Limits =
        WindowsInterop.JobLimitKillOnJobClose |
        WindowsInterop.JobLimitBreakawayOk |
        WindowsInterop.JobLimitSilentBreakawayOk |
        WindowsInterop.JobLimitDieOnUnhandledException;

    private readonly IJobObjects _kernel;
    private readonly Lock _gate = new();

    /// <summary>Jobs replaced after a refused assignment; held so their servers keep their owner tie.</summary>
    private readonly List<SafeJobHandle> _replaced = [];

    private SafeJobHandle? _current;

    /// <summary>Initializes a job over the kernel's calls; nothing is created until the first assignment.</summary>
    /// <param name="kernel">The kernel's job-object calls.</param>
    public KillOnCloseJob(IJobObjects kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _kernel = kernel;
    }

    /// <summary>Gets the job every start of this process shares.</summary>
    public static KillOnCloseJob ForCurrentProcess { get; } = new(new JobObjects());

    /// <summary>Gets the job later starts are assigned to, once one exists; for the tests that ask the kernel about membership.</summary>
    internal SafeJobHandle? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public JobAssignment Assign(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            // A creation that throws leaves the field empty, so the next start creates afresh.
            _current ??= _kernel.Create(Limits);
            var error = _kernel.Assign(_current, process);
            if (error == WindowsInterop.InvalidParameter)
            {
                var fresh = _kernel.Create(Limits);
                error = _kernel.Assign(fresh, process);
                if (error == 0)
                {
                    _replaced.Add(_current);
                    _current = fresh;
                }
                else
                {
                    // The fresh job took nothing, so closing it ends nothing.
                    fresh.Dispose();
                }
            }

            return error switch
            {
                0 => JobAssignment.Assigned,
                WindowsInterop.AccessDenied => JobAssignment.Refused,
                _ => throw new Win32Exception(error),
            };
        }
    }
}
