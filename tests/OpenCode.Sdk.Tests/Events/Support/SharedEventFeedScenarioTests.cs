using TUnit.Assertions.Exceptions;

namespace OpenCode.Sdk.Tests.Events.Support;

public sealed class SharedEventFeedScenarioTests
{
    [Test]
    public async Task DisposeAsync_Should_Release_And_Join_All_Owned_Work()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var attempt = scenario.CurrentAttempt!;
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
