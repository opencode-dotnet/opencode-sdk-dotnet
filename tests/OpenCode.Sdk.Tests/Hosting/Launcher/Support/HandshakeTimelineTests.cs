using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>
/// The handshake's timeline, on a clock the test moves: a timeout reports each step observed done before it, and how
/// long after the handshake began the timeout was observed.
/// </summary>
public sealed class HandshakeTimelineTests
{
    [Test]
    public async Task Describe_Should_Name_Each_Observed_Step_In_Order_And_When_The_Timeout_Was_Observed()
    {
        var time = new ManualTimeProvider();
        var timeline = new HandshakeTimeline(time);

        time.Advance(TimeSpan.FromMilliseconds(840));
        timeline.Completed("the root to connect back");
        time.Advance(TimeSpan.FromMilliseconds(1_660));
        timeline.Completed("the root to authenticate");
        time.Advance(TimeSpan.FromMilliseconds(12_500));

        await Assert.That(timeline.Describe())
            .IsEqualTo("Steps observed done before the timeout: the root to connect back at 0.84 s, the root to authenticate at 2.50 s; the timeout was observed 15.00 s after the handshake began.");
    }

    [Test]
    public async Task Describe_Should_Say_None_When_No_Step_Was_Observed_Done()
    {
        var time = new ManualTimeProvider();
        var timeline = new HandshakeTimeline(time);

        time.Advance(TimeSpan.FromSeconds(15));

        await Assert.That(timeline.Describe()).IsEqualTo("Steps observed done before the timeout: none; the timeout was observed 15.00 s after the handshake began.");
    }
}
