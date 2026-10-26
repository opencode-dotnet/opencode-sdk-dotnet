using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.ColdStart;

/// <summary>The live bus's first idle event after its opener, against materialization alone.</summary>
/// <remarks>
/// Rows report first-use time only. The allocation column is not a first-use figure because
/// BenchmarkDotNet measures allocation in a later, warm invocation.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.ColdStart, launchCount: 5, warmupCount: 0, iterationCount: 1)]
public class ColdStartEventStreamBenchmarks
{
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _body = [];
    private string _frame = string.Empty;
    private IEvent? _subscribed;
    private IEvent? _materialized;

    [GlobalSetup]
    public void Setup()
    {
        var idle = BenchmarkFixtures.SessionIdleEventBody();
        _body = BenchmarkFixtures.EventStream([BenchmarkFixtures.ConnectedEventBody(), idle]);
        _frame = Encoding.UTF8.GetString(idle);
    }

    /// <summary>The first idle event through a fresh client and a subscription whose reads suspend.</summary>
    [Benchmark]
    public async Task<IEvent?> SubscribeSuspendingAsync()
    {
        using var handler = new CannedResponseHandler(_body, "text/event-stream", suspendingChunkBytes: 1460);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenCodeClient(httpClient, Options);
        await using var events = client.Events.SubscribeAsync().GetAsyncEnumerator();
        _ = await events.MoveNextAsync().ConfigureAwait(false);
        _subscribed = await events.MoveNextAsync().ConfigureAwait(false) ? events.Current : null;
        return _subscribed;
    }

    /// <summary>The first materialization through the live-bus union, from the idle frame's string.</summary>
    [Benchmark]
    public IEvent? DeserializeFrames() => _materialized = JsonSerializer.Deserialize(_frame, OpenCodeJsonContext.Default.IEvent);

    [IterationCleanup(Targets = new[] { nameof(SubscribeSuspendingAsync) })]
    public void CheckSubscription() => RequireIdle(_subscribed);

    [IterationCleanup(Targets = new[] { nameof(DeserializeFrames) })]
    public void CheckMaterialization() => RequireIdle(_materialized);

    private static void RequireIdle(IEvent? result)
    {
        if (result is not SessionIdle)
        {
            throw new InvalidOperationException("The cold bus fixture did not materialize the expected idle event.");
        }
    }
}
