namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>
/// The server options refuse a timeout outside the range every supported timer can represent when
/// it is set, so a value that would fail only after a server was spawned never reaches a start.
/// </summary>
public sealed class OpenCodeServerOptionsTests
{
    private static readonly TimeSpan TimerCeiling = TimeSpan.FromMilliseconds(int.MaxValue);

    [Test]
    public async Task ReadinessTimeout_Should_Accept_Its_Range_And_Infinite()
    {
        foreach (var value in new[] { TimeSpan.FromTicks(1), TimeSpan.FromSeconds(60), TimerCeiling, Timeout.InfiniteTimeSpan })
        {
            var options = new OpenCodeServerOptions { ReadinessTimeout = value };

            await Assert.That(options.ReadinessTimeout).IsEqualTo(value);
        }
    }

    [Test]
    public async Task ReadinessTimeout_Should_Refuse_Zero_Negative_And_Past_The_Ceiling()
    {
        foreach (var value in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromMilliseconds(-2), TimerCeiling + TimeSpan.FromTicks(1) })
        {
            var options = new OpenCodeServerOptions();

            _ = await Assert.That(() => options.ReadinessTimeout = value).Throws<ArgumentOutOfRangeException>();
            await Assert.That(options.ReadinessTimeout).IsEqualTo(TimeSpan.FromSeconds(60));
        }
    }

    [Test]
    public async Task GracefulShutdownTimeout_Should_Accept_Zero_To_The_Ceiling()
    {
        foreach (var value in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3), TimerCeiling })
        {
            var options = new OpenCodeServerOptions { GracefulShutdownTimeout = value };

            await Assert.That(options.GracefulShutdownTimeout).IsEqualTo(value);
        }
    }

    /// <summary>The end of a server is always bounded, so the infinite sentinel is refused like any negative value.</summary>
    [Test]
    public async Task GracefulShutdownTimeout_Should_Refuse_Negative_Infinite_And_Past_The_Ceiling()
    {
        foreach (var value in new[] { TimeSpan.FromTicks(-1), Timeout.InfiniteTimeSpan, TimerCeiling + TimeSpan.FromTicks(1) })
        {
            var options = new OpenCodeServerOptions();

            _ = await Assert.That(() => options.GracefulShutdownTimeout = value).Throws<ArgumentOutOfRangeException>();
            await Assert.That(options.GracefulShutdownTimeout).IsEqualTo(TimeSpan.FromSeconds(3));
        }
    }
}
