using System.ComponentModel;
using System.Diagnostics;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// <see cref="ProcessTreeTerminator"/> reports every outcome of the platform's tree kill as a
/// result and never raises: a teardown that issued a kill always continues past it.
/// </summary>
public sealed class ProcessTreeTerminatorTests
{
    [Test]
    public async Task TryKill_Should_Report_Incomplete_When_A_Process_Of_The_Tree_Refused_The_Kill()
    {
        var kill = Substitute.For<IProcessTreeKill>();
        _ = kill.Kill(Arg.Any<Process>()).Throws(new AggregateException(new Win32Exception()));
        using var process = new Process();

        var result = new ProcessTreeTerminator(kill).TryKill(process);

        await Assert.That(result).IsEqualTo(ProcessTreeKillResult.Incomplete);
    }

    [Test]
    public async Task TryKill_Should_Report_Nothing_To_End_When_The_Root_Already_Exited()
    {
        var kill = Substitute.For<IProcessTreeKill>();
        _ = kill.Kill(Arg.Any<Process>()).Throws(new InvalidOperationException());
        using var process = new Process();

        var result = new ProcessTreeTerminator(kill).TryKill(process);

        await Assert.That(result).IsEqualTo(ProcessTreeKillResult.NothingToEnd);
    }

    [Test]
    public async Task TryKill_Should_Pass_On_The_Platform_Result()
    {
        var kill = Substitute.For<IProcessTreeKill>();
        _ = kill.Kill(Arg.Any<Process>()).Returns(ProcessTreeKillResult.Incomplete);
        using var process = new Process();

        var result = new ProcessTreeTerminator(kill).TryKill(process);

        await Assert.That(result).IsEqualTo(ProcessTreeKillResult.Incomplete);
    }
}
