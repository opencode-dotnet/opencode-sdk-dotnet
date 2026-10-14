namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtyConnectOptionsTests
{
    [Test]
    public async Task SendTimeout_Should_Default_To_Thirty_Seconds_And_Accept_A_Custom_Budget()
    {
        await Assert.That(new PtyConnectOptions().SendTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
        var options = new PtyConnectOptions { SendTimeout = TimeSpan.FromSeconds(2) };
        await Assert.That(options.SendTimeout).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    [Arguments(-10_000L)]
    [Arguments(0L)]
    [Arguments(9_999L)]
    [Arguments(21_474_836_470_001L)]
    public async Task SendTimeout_Should_Refuse_An_Unrepresentable_Finite_Budget(long ticks)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _ = new PtyConnectOptions { SendTimeout = TimeSpan.FromTicks(ticks) });
        await Task.CompletedTask;
    }

    /// <summary>The largest cursor the server's JS safe-integer guard accepts.</summary>
    private const long MaximumCursor = 9_007_199_254_740_991;

    public static IEnumerable<Func<long>> RefusedCursors() =>
    [
        static () => -2,
        static () => long.MinValue,
        static () => MaximumCursor + 1,
        static () => long.MaxValue,
    ];

    public static IEnumerable<Func<long>> AcceptedCursors() =>
    [
        static () => -1,
        static () => 0,
        static () => 1,
        static () => MaximumCursor,
    ];

    [Test]
    [MethodDataSource(nameof(RefusedCursors))]
    public async Task Cursor_Should_Refuse_A_Value_Outside_The_Servers_Accepted_Range(long cursor)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PtyConnectOptions { Cursor = cursor });

        await Task.CompletedTask;
    }

    [Test]
    [MethodDataSource(nameof(AcceptedCursors))]
    public async Task Cursor_Should_Accept_A_Value_Inside_The_Servers_Accepted_Range(long cursor)
    {
        var options = new PtyConnectOptions { Cursor = cursor };

        await Assert.That(options.Cursor).IsEqualTo(cursor);
    }

    [Test]
    public async Task Cursor_Should_Default_To_The_Full_Replay()
    {
        var options = new PtyConnectOptions();

        await Assert.That(options.Cursor).IsNull();
        await Assert.That(options.Location).IsNull();
    }
}
