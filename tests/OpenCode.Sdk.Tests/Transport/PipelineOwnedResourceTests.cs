using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PipelineOwnedResourceTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Dispose_Should_Dispose_Owned_Resources_In_Reverse_Order_Once_Before_The_Transport(int failures)
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, ownsHttpClient: true);
        var ledger = new List<string>();
        var transportStates = new List<bool>();
        var firstDisposals = 0;
        var secondDisposals = 0;
        var firstFailure = new InvalidOperationException("first resource");
        var secondFailure = new InvalidOperationException("second resource");
        using var first = new OwnedResource(() =>
        {
            ledger.Add("first");
            transportStates.Add(handler.IsDisposed);
            firstDisposals++;
            if (firstDisposals == 1 && failures == 2)
            {
                throw firstFailure;
            }
        });
        using var second = new OwnedResource(() =>
        {
            ledger.Add("second");
            transportStates.Add(handler.IsDisposed);
            secondDisposals++;
            if (secondDisposals == 1 && failures != 0)
            {
                throw secondFailure;
            }
        });
        pipeline.RegisterOwned(first);
        pipeline.RegisterOwned(second);
        Exception? failure = null;
        try
        {
            pipeline.Dispose();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        pipeline.Dispose();
        await Assert.That(ledger).IsEquivalentTo(["second", "first"], CollectionOrdering.Matching);
        await Assert.That(transportStates).IsEquivalentTo([false, false], CollectionOrdering.Matching);
        await Assert.That(handler.IsDisposed).IsTrue();
        await Assert.That(firstDisposals).IsEqualTo(1);
        await Assert.That(secondDisposals).IsEqualTo(1);
        if (failures == 0)
        {
            await Assert.That(failure).IsNull();
        }
        else if (failures == 1)
        {
            await Assert.That(failure).IsSameReferenceAs(secondFailure);
        }
        else
        {
            await Assert.That(failure).IsSameReferenceAs(firstFailure);
        }

        using var late = new OwnedResource(static () => { });
        _ = await Assert.That(() => pipeline.RegisterOwned(late)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Dispose_Should_Close_Registration_And_Return_Concurrent_Disposals_While_Cleanup_Is_Held()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, ownsHttpClient: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var count = 0;
        using var held = new OwnedResource(() =>
        {
            count++;
            _ = entered.TrySetResult();
            if (!release.Wait(HangGuard.Bound))
            {
                throw new TimeoutException("The test did not release owned cleanup.");
            }
        });
        pipeline.RegisterOwned(held);
        var disposing = DedicatedThread.Run(pipeline.Dispose);
        try
        {
            await entered.Task.WaitAsync(HangGuard.Bound);
            pipeline.Dispose();
            await Assert.That(handler.IsDisposed).IsFalse();
            using var refused = new OwnedResource(static () => { });
            _ = await Assert.That(() => pipeline.RegisterOwned(refused)).Throws<ObjectDisposedException>();
            await Assert.That(count).IsEqualTo(1);
        }
        finally
        {
            release.Set();
            await disposing.WaitAsync(HangGuard.Bound);
        }

        await Assert.That(handler.IsDisposed).IsTrue();
        await Assert.That(count).IsEqualTo(1);
    }

    private sealed class OwnedResource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
