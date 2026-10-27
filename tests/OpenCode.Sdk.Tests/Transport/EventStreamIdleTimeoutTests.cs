using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.Tests.Transport.Support;

namespace OpenCode.Sdk.Tests.Transport;

/// <summary>
/// <see cref="OpenCodeClientOptions.EventStreamIdleTimeout"/>: the live event stream's idle window,
/// 45 seconds unless set, as upstream's <c>idleTimeout</c> option. Any window a timer can run is
/// admitted; a window that never ends is not, because upstream's client has none.
/// </summary>
public sealed class EventStreamIdleTimeoutTests
{
    /// <summary>Minus one millisecond is <see cref="Timeout.InfiniteTimeSpan"/> itself, which is refused too.</summary>
    [Test]
    [Arguments(0L)]
    [Arguments(-TimeSpan.TicksPerMillisecond)]
    [Arguments(-TimeSpan.TicksPerMillisecond * 2)]
    [Arguments((int.MaxValue * TimeSpan.TicksPerMillisecond) + 1)]
    public async Task Options_Should_Refuse_A_Window_That_Is_Empty_Infinite_Or_Beyond_A_Timer(long ticks)
    {
        var value = TimeSpan.FromTicks(ticks);

        var refusal = await Assert.That(() => new OpenCodeClientOptions { EventStreamIdleTimeout = value }).Throws<ArgumentOutOfRangeException>();

        await Assert.That(refusal!.ParamName).IsEqualTo("value");
    }

    /// <summary>The idle-window refusal names its finite range without offering an infinite window.</summary>
    [Test]
    public async Task Options_Should_Report_The_Exact_Idle_Window_Refusal() =>
        await OptionRefusal.AssertAsync(() => _ = new OpenCodeClientOptions { EventStreamIdleTimeout = TimeSpan.Zero }, TimeSpan.Zero,
            "The event stream idle window must be above zero and at most int.MaxValue milliseconds.", "value");

    [Test]
    [Arguments(1L)]
    [Arguments(int.MaxValue * TimeSpan.TicksPerMillisecond)]
    public async Task Options_Should_Accept_A_Window_A_Timer_Can_Run(long ticks)
    {
        var value = TimeSpan.FromTicks(ticks);

        await Assert.That(new OpenCodeClientOptions { EventStreamIdleTimeout = value }.EventStreamIdleTimeout).IsEqualTo(value);
    }

    /// <summary>Unset is the default: the pipeline then runs upstream's 45 s, which the reconnect tests pin by behaviour.</summary>
    [Test]
    public async Task Options_Should_Leave_The_Window_Unset_By_Default() =>
        await Assert.That(new OpenCodeClientOptions().EventStreamIdleTimeout).IsNull();

    /// <summary>A passed window is named with the window that passed, down to the single tick the option accepts.</summary>
    [Test]
    [Arguments(15_000_000L, "1.5")]
    [Arguments(1L, "0.0000001")]
    public async Task A_Passed_Window_Should_Be_Named_With_Its_Own_Length(long ticks, string seconds)
    {
        using var attempt = new StreamAttempt(TimeProvider.System, TimeSpan.FromTicks(ticks), CancellationToken.None);
        attempt.OnFrame(TestFrames.FromText(ServerSentEvent.DefaultName, ScriptedEventServer.Connected));
        attempt.Establish();

        var failure = attempt.Describe(new OperationCanceledException());

        await Assert.That(failure.Message).IsEqualTo($"The opencode event stream received nothing for {seconds} seconds.");
    }
}
