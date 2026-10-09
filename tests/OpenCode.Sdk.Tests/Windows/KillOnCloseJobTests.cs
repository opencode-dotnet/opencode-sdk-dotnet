using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Windows;

/// <summary>
/// The launcher's job decides over the kernel's answers: libuv's four limits, a job created on
/// the first assignment and kept, a creation failure that fails the start and is not kept, a refusal
/// with <c>ERROR_ACCESS_DENIED</c> that is tolerated, and an assignment refused with
/// <c>ERROR_INVALID_PARAMETER</c> tried once more in a fresh job while the old one stays held. The
/// kernel is substituted for these, since no kernel fails on cue; the handles it hands out are
/// empty, so nothing here reaches a real job. One test creates a real job, on Windows, to show
/// that its handle is not inheritable.
/// </summary>
public sealed class KillOnCloseJobTests
{
    private const int Success = 0;
    private const int AccessDenied = 5;
    private const int InvalidParameter = 87;
    private const int NotEnoughQuota = 1816;

    [Test]
    public async Task Assign_Should_Create_The_Job_Once_With_Libuvs_Four_Limits()
    {
        var kernel = Kernel();
        var job = new KillOnCloseJob(kernel);
        using var first = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        using var second = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var assignments = new[] { job.Assign(first), job.Assign(second) };

        await Assert.That(assignments).All().Satisfy(static assignment => assignment.IsEqualTo(JobAssignment.Assigned));
        _ = kernel.Received(1).Create(Arg.Any<uint>());
        _ = kernel.Received(1).Create(0x2000 | 0x1000 | 0x800 | 0x400);
    }

    [Test]
    public async Task Assign_Should_Report_A_Refusal_With_Access_Denied_Rather_Than_Raise_It()
    {
        var kernel = Kernel();
        _ = kernel.Assign(Arg.Any<SafeJobHandle>(), Arg.Any<SafeProcessHandle>()).Returns(AccessDenied);
        using var process = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var assignment = new KillOnCloseJob(kernel).Assign(process);

        await Assert.That(assignment).IsEqualTo(JobAssignment.Refused);
    }

    [Test]
    public async Task Assign_Should_Raise_Any_Other_Refusal()
    {
        var kernel = Kernel();
        _ = kernel.Assign(Arg.Any<SafeJobHandle>(), Arg.Any<SafeProcessHandle>()).Returns(NotEnoughQuota);
        using var process = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var failure = await Assert.That(() => new KillOnCloseJob(kernel).Assign(process)).Throws<Win32Exception>();

        await Assert.That(failure!.NativeErrorCode).IsEqualTo(NotEnoughQuota);
    }

    /// <summary>
    /// A creation that fails fails that start, and the next start creates again: a transient
    /// failure (quota, low resources) never fails every later start of the process.
    /// </summary>
    [Test]
    public async Task Assign_Should_Not_Keep_A_Failed_Creation()
    {
        var kernel = Substitute.For<IJobObjects>();
        using var created = new SafeJobHandle();
        _ = kernel.Create(Arg.Any<uint>()).Returns(
            _ => throw new Win32Exception(NotEnoughQuota),
            _ => created);
        var job = new KillOnCloseJob(kernel);
        using var process = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var failure = await Assert.That(() => job.Assign(process)).Throws<Win32Exception>();
        var retried = job.Assign(process);

        await Assert.That(failure!.NativeErrorCode).IsEqualTo(NotEnoughQuota);
        await Assert.That(retried).IsEqualTo(JobAssignment.Assigned);
        await Assert.That(job.Current).IsSameReferenceAs(created);
        _ = kernel.Received(2).Create(Arg.Any<uint>());
    }

    /// <summary>
    /// An assignment refused with <c>ERROR_INVALID_PARAMETER</c> is tried once more in a fresh job,
    /// which then serves every later start; the old job is neither closed nor used again, because a
    /// server may be in it.
    /// </summary>
    [Test]
    public async Task Assign_Should_Retry_An_Invalid_Parameter_Refusal_Once_In_A_Fresh_Job_And_Keep_The_Old_One()
    {
        var kernel = Substitute.For<IJobObjects>();
        using var poisoned = new SafeJobHandle();
        using var fresh = new SafeJobHandle();
        _ = kernel.Create(Arg.Any<uint>()).Returns(poisoned, fresh);
        _ = kernel.Assign(poisoned, Arg.Any<SafeProcessHandle>()).Returns(InvalidParameter);
        _ = kernel.Assign(fresh, Arg.Any<SafeProcessHandle>()).Returns(Success);
        var job = new KillOnCloseJob(kernel);
        using var first = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        using var later = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var assignment = job.Assign(first);
        var laterAssignment = job.Assign(later);

        await Assert.That(assignment).IsEqualTo(JobAssignment.Assigned);
        await Assert.That(laterAssignment).IsEqualTo(JobAssignment.Assigned);
        await Assert.That(job.Current).IsSameReferenceAs(fresh);
        await Assert.That(poisoned.IsClosed).IsFalse();
        _ = kernel.Received(1).Assign(poisoned, Arg.Any<SafeProcessHandle>());
        _ = kernel.Received(2).Assign(fresh, Arg.Any<SafeProcessHandle>());
    }

    /// <summary>
    /// A fresh job that refuses the process too fails the start and is closed, since it holds
    /// nothing; the job of the running servers stays the one later starts use. A command the kernel
    /// always refuses this way therefore never displaces that job, and holds no job per start.
    /// </summary>
    [Test]
    public async Task Assign_Should_Raise_A_Second_Invalid_Parameter_Refusal_And_Keep_The_Job_In_Use()
    {
        var kernel = Substitute.For<IJobObjects>();
        using var serving = new SafeJobHandle();
        using var refusing = new SafeJobHandle();
        _ = kernel.Create(Arg.Any<uint>()).Returns(serving, refusing);
        using var accepted = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        using var refused = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        _ = kernel.Assign(Arg.Any<SafeJobHandle>(), accepted).Returns(Success);
        _ = kernel.Assign(Arg.Any<SafeJobHandle>(), refused).Returns(InvalidParameter);
        var job = new KillOnCloseJob(kernel);

        _ = job.Assign(accepted);
        var failure = await Assert.That(() => job.Assign(refused)).Throws<Win32Exception>();
        var afterwards = job.Assign(accepted);

        await Assert.That(failure!.NativeErrorCode).IsEqualTo(InvalidParameter);
        await Assert.That(afterwards).IsEqualTo(JobAssignment.Assigned);
        await Assert.That(job.Current).IsSameReferenceAs(serving);
        await Assert.That(serving.IsClosed).IsFalse();
        await Assert.That(refusing.IsClosed).IsTrue();
        _ = kernel.Received(2).Create(Arg.Any<uint>());
        _ = kernel.Received(2).Assign(serving, accepted);
    }

    /// <summary>
    /// An inheritable job handle would reach any child the host starts with handle inheritance, and
    /// that child would then hold the job, and every server in it, open past the host's own end.
    /// </summary>
    [Test]
    public async Task Create_Should_Return_A_Job_Handle_No_Child_Can_Inherit()
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no job objects; the launcher places its server in a session instead");
            return;
        }

        using var job = new JobObjects().Create(KillOnCloseJob.Limits);

        await Assert.That(job.IsInvalid).IsFalse();
        await Assert.That(WindowsProcessProbe.IsInheritable(job)).IsFalse();
        BranchReport.Print("Windows — the job handle carries no inherit flag");
    }

    private static IJobObjects Kernel()
    {
        var kernel = Substitute.For<IJobObjects>();
        _ = kernel.Create(Arg.Any<uint>()).Returns(_ => new SafeJobHandle());
        _ = kernel.Assign(Arg.Any<SafeJobHandle>(), Arg.Any<SafeProcessHandle>()).Returns(Success);
        return kernel;
    }
}
