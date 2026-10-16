using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The reservation's one hard promise: a pair is two different ports. A single reservation cannot
/// promise anything about a later one - it releases the port before returning - so the pair door
/// is what the drive manifest depends on.
/// </summary>
public sealed class LoopbackPortReservationTests
{
    /// <summary>
    /// Repeated because the failure it guards against is a race the OS wins only sometimes:
    /// reserving one at a time returned an equal pair once in a real suite run, and a single
    /// sample would have passed there too.
    /// </summary>
    [Test]
    public async Task ReservePair_Should_Return_Two_Distinct_Ports()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var (first, second) = LoopbackPortReservation.ReservePair();

            await Assert.That(first).IsNotEqualTo(second);
            await Assert.That(first).IsGreaterThanOrEqualTo(1);
            await Assert.That(second).IsGreaterThanOrEqualTo(1);
        }
    }
}
