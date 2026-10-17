using System.Globalization;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class EnsureServiceContextTests
{
    [Test]
    [Arguments(45278)]
    [Arguments(49374)]
    public async Task RefuseRealDaemonPort_Should_Refuse_A_Port_That_Could_Belong_To_A_Real_Daemon(int port)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => EnsureServiceContext.RefuseRealDaemonPort(port));

        await Assert.That(exception.Message).IsEqualTo(
            $"The reserved loopback port is {port.ToString(CultureInfo.InvariantCulture)} and could belong to a real daemon "
            + "(the Windows release default or an explicitly configured service); the environment is contaminated. "
            + "Refusing to run rather than touch it.");
    }

    [Test]
    [Arguments(45277)]
    [Arguments(45279)]
    [Arguments(49373)]
    [Arguments(49375)]
    public async Task RefuseRealDaemonPort_Should_Keep_Any_Other_Reserved_Port(int port)
    {
        await Assert.That(EnsureServiceContext.RefuseRealDaemonPort(port)).IsEqualTo(port);
    }
}
