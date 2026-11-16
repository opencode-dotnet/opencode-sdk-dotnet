using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigStoreRetryTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriteAsync_Should_Retry_Twenty_Times_Ten_Milliseconds_Apart_And_Retain_A_Failed_Temporary(bool exhausted)
    {
        var clock = new ManualTimeProvider();
        var scenario = new ServiceConfigScenario { Time = clock };
        var attempts = 0;
        IOException? last = null;
        scenario.Rename = (source, destination) =>
        {
            attempts++;
            if (exhausted || attempts <= 20)
            {
                last = new IOException("rename refused");
                throw last;
            }
            scenario.Adapter.Rename(source, destination);
        };
        using var caller = new CancellationTokenSource();
        using var waits = new CancellationTokenSource();
        var operation = scenario.Store.WriteAsync(scenario.Paths, new ServiceConfigDocument(), caller.Token);
        try
        {
            for (var failure = 1; failure <= 20; failure++)
            {
                var timer = clock.WaitForTimerAsync(HangGuard, waits.Token);
                await Assert.That(await Task.WhenAny(timer, operation).WaitAsync(HangGuard)).IsSameReferenceAs(timer);
                await timer;
                await Assert.That(attempts).IsEqualTo(failure);
                await Assert.That(clock.ArmedIn(TimeSpan.FromMilliseconds(10))).IsEqualTo(1);
                clock.Advance(TimeSpan.FromMilliseconds(9));
                await Assert.That(attempts).IsEqualTo(failure);
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }
            if (exhausted)
            {
                var error = await Assert.That(operation).Throws<OpenCodeServerException>();
                await Assert.That(error).IsNotNull();
                await Assert.That(error.InnerException).IsSameReferenceAs(last);
                await Assert.That(error.Message).Contains(scenario.Paths.ConfigFile!);
            }
            else
            {
                await operation;
            }
            await Assert.That(attempts).IsEqualTo(21);
            if (exhausted)
            {
                await Assert.That(scenario.Inventory()).Contains("file:" + scenario.Paths.ConfigFile + ".12345.tmp");
            }
            else
            {
                await Assert.That(scenario.Inventory()).DoesNotContain("file:" + scenario.Paths.ConfigFile + ".12345.tmp");
            }
        }
        finally
        {
            await waits.CancelOnWorkerAsync();
            await caller.CancelOnWorkerAsync();
            if (!operation.IsCompleted)
            {
                _ = await Assert.That(operation).Throws<OperationCanceledException>();
            }
        }
    }

    [Test]
    public async Task WriteAsync_Should_Propagate_Retry_Cancellation_With_The_Caller_Token()
    {
        var clock = new ManualTimeProvider();
        var scenario = new ServiceConfigScenario { Time = clock, Rename = (_, _) => throw new IOException("rename refused") };
        using var caller = new CancellationTokenSource();
        using var waits = new CancellationTokenSource();
        var operation = scenario.Store.WriteAsync(scenario.Paths, new ServiceConfigDocument(), caller.Token);
        var timer = clock.WaitForTimerAsync(HangGuard, waits.Token);
        try
        {
            await Assert.That(await Task.WhenAny(timer, operation).WaitAsync(HangGuard)).IsSameReferenceAs(timer);
            await timer;
            await caller.CancelOnWorkerAsync();
            var armed = clock.ArmedIn(TimeSpan.FromMilliseconds(10));
            clock.Advance(TimeSpan.FromMilliseconds(10));
            var error = await Assert.That(operation).Throws<OperationCanceledException>();
            await Assert.That(error).IsNotNull();
            await Assert.That(error.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(armed).IsEqualTo(0);
            await Assert.That(scenario.Calls.Count(static call => call.StartsWith("Rename:", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally
        {
            await waits.CancelOnWorkerAsync();
            await caller.CancelOnWorkerAsync();
            clock.Advance(TimeSpan.FromMilliseconds(10));
            _ = await Task.WhenAny(operation).WaitAsync(HangGuard);
        }
    }
}
