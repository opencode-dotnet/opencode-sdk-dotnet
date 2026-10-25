using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Terminal;

public sealed class TerminalSendTimeoutTests
{
    /// <summary>The floor bound is exactly one millisecond, the smallest accepted budget.</summary>
    private const long FloorTicks = 10_000;

    /// <summary>The ceiling bound is exactly int.MaxValue milliseconds, the largest accepted budget.</summary>
    private const long CeilingTicks = 21_474_836_470_000;

    [Test]
    [Arguments(FloorTicks)]
    [Arguments(CeilingTicks)]
    public async Task Constructor_Should_Accept_A_Budget_At_Either_Bound(long ticks)
    {
        var timeout = new TerminalSendTimeout(TimeSpan.FromTicks(ticks));

        await Assert.That(timeout.Value).IsEqualTo(TimeSpan.FromTicks(ticks));
    }

    [Test]
    [Arguments(FloorTicks - 1)]
    [Arguments(CeilingTicks + 1)]
    public async Task Constructor_Should_Refuse_A_Budget_Just_Outside_Either_Bound(long ticks)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new TerminalSendTimeout(TimeSpan.FromTicks(ticks)));
        await Task.CompletedTask;
    }
}
