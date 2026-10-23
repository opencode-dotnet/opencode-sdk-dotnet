using System.Globalization;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class EnsureServiceContextTests
{
    [Test]
    [Arguments(28894)]
    [Arguments(45278)]
    [Arguments(49374)]
    public async Task RefuseRealDaemonPort_Should_Refuse_A_Port_That_Could_Belong_To_A_Real_Daemon(int port)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => EnsureServiceContext.RefuseRealDaemonPort(port));

        await Assert.That(exception.Message).IsEqualTo(RefusalMessage(port));
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

    [Test]
    [Arguments(28894, 0)]
    [Arguments(45278, 0)]
    [Arguments(49374, 0)]
    [Arguments(50000, 50000)]
    public async Task ReserveUnguardedPort_Should_Take_A_New_Reservation_While_Holding_The_Refused_One(int refused, int except)
    {
        var holds = new FakeHolds(refused, 50001);
        int[] heldWhileRetaking = [refused];

        var port = EnsureServiceContext.ReserveUnguardedPort(holds.Hold, except);

        await Assert.That(port).IsEqualTo(50001);
        await Assert.That(holds.HeldAtEachReservation[1]).IsEquivalentTo(heldWhileRetaking, CollectionOrdering.Matching);
        await Assert.That(holds.Held).IsEmpty();
    }

    [Test]
    public async Task ReserveUnguardedPort_Should_Refuse_The_Run_When_Every_Reservation_Lands_On_A_Guarded_Port()
    {
        var holds = new FakeHolds([.. Enumerable.Repeat(49374, EnsureServiceContext.ReservationAttempts)]);

        var exception = Assert.Throws<InvalidOperationException>(() => EnsureServiceContext.ReserveUnguardedPort(holds.Hold));

        await Assert.That(exception.Message).IsEqualTo(RefusalMessage(49374));
        await Assert.That(holds.HeldAtEachReservation).Count().IsEqualTo(EnsureServiceContext.ReservationAttempts);
        await Assert.That(holds.Held).IsEmpty();
    }

    [Test]
    public async Task ReserveUnguardedPort_Should_Fail_When_Every_Reservation_Lands_On_The_Callers_Own_Port()
    {
        var holds = new FakeHolds([.. Enumerable.Repeat(50000, EnsureServiceContext.ReservationAttempts)]);

        var exception = Assert.Throws<InvalidOperationException>(() => EnsureServiceContext.ReserveUnguardedPort(holds.Hold, 50000));

        await Assert.That(exception.Message).IsEqualTo(
            $"Every one of {EnsureServiceContext.ReservationAttempts.ToString(CultureInfo.InvariantCulture)} reserved loopback ports "
            + "was refused; the last was 50000, the port the caller already uses.");
        await Assert.That(holds.Held).IsEmpty();
    }

    private static string RefusalMessage(int port) =>
        $"The reserved loopback port is {port.ToString(CultureInfo.InvariantCulture)} and could belong to a real daemon "
        + "(the Windows release default or an explicitly configured service); the environment is contaminated. "
        + "Refusing to run rather than touch it.";

    /// <summary>
    /// Hands out the given ports in order and records which ports were still held at each
    /// reservation; a port leaves <see cref="Held"/> when its hold is disposed.
    /// </summary>
    private sealed class FakeHolds(params int[] ports)
    {
        private readonly Queue<int> _ports = new(ports);

        public List<int> Held { get; } = [];

        public List<int[]> HeldAtEachReservation { get; } = [];

        public HeldLoopbackPort Hold()
        {
            HeldAtEachReservation.Add([.. Held]);
            var port = _ports.Dequeue();
            Held.Add(port);
            return new HeldLoopbackPort(port, () => _ = Held.Remove(port));
        }
    }
}
