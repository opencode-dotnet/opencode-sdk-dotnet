using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

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

    [Test]
    [Arguments(1L)]
    [Arguments(int.MaxValue * TimeSpan.TicksPerMillisecond)]
    public async Task Options_Should_Accept_A_Window_A_Timer_Can_Run(long ticks)
    {
        var value = TimeSpan.FromTicks(ticks);

        await Assert.That(new OpenCodeClientOptions { EventStreamIdleTimeout = value }.EventStreamIdleTimeout).IsEqualTo(value);
    }

    [Test]
    public async Task An_Unset_Window_Should_Be_Upstreams_45_Seconds()
    {
        using var httpClient = new HttpClient();

        using var pipeline = PipelineFactory.Create(httpClient);

        await Assert.That(new OpenCodeClientOptions().EventStreamIdleTimeout).IsNull();
        await Assert.That(pipeline.EventStreamIdleWindow).IsEqualTo(TimeSpan.FromSeconds(45));
    }

    [Test]
    public async Task A_Set_Window_Should_Reach_The_Pipeline()
    {
        using var httpClient = new HttpClient();

        using var pipeline = PipelineFactory.Create(httpClient, eventStreamIdleTimeout: TimeSpan.FromSeconds(20));

        await Assert.That(pipeline.EventStreamIdleWindow).IsEqualTo(TimeSpan.FromSeconds(20));
    }

    /// <summary>A window passing is named with the window that passed, fractions of a second included.</summary>
    [Test]
    public async Task A_Passed_Window_Should_Be_Named_With_Its_Own_Length()
    {
        using var attempt = new StreamAttempt(TimeProvider.System, TimeSpan.FromMilliseconds(1500), CancellationToken.None);
        attempt.OnFrame(new ServerSentEvent(ServerSentEvent.DefaultName, ScriptedEventServer.Connected));
        attempt.Establish();

        var failure = attempt.Describe(new OperationCanceledException());

        await Assert.That(failure.Message).IsEqualTo("The opencode event stream received nothing for 1.5 seconds.");
    }
}
