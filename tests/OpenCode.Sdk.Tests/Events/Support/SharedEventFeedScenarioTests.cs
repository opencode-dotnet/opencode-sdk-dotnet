using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Exceptions;

namespace OpenCode.Sdk.Tests.Events.Support;

public sealed class SharedEventFeedScenarioTests
{
    [Test]
    public async Task DisposeAsync_Should_Release_And_Join_All_Owned_Work()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator();
        var first = events.MoveNextAsync().AsTask();
        var attempt = await scenario.Attempts.NextOpenAsync();
        await attempt.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await first.WaitAsync(HangGuard.Bound)).IsTrue();
        var source = scenario.Feed.GetCurrentSource()!;

        _ = await Assert.That(async () =>
        {
            try
            {
                await Assert.That(events.Current.Id).IsEqualTo("forced assertion failure");
            }
            finally
            {
                await scenario.DisposeAsync();
            }
        }).Throws<AssertionException>();

        await Assert.That(attempt.CleanupReleased.Task.Status).IsEqualTo(TaskStatus.RanToCompletion);
        await Assert.That(source.IsReleased).IsTrue();
        await Assert.That(source.RunnerObservation!.Status).IsEqualTo(TaskStatus.RanToCompletion);
    }
}
