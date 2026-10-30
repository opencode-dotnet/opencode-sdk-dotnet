using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// A prompt's live bus: the suspending subscription, polymorphic decode alone, and concrete
/// decode without discriminator dispatch, all over the same mixed turn frames.
/// </summary>
/// <remarks>
/// Wire bytes include the whole connection; items count only turn frames. The subscription
/// row's Alloc B/item includes one server.connected opener per connection. Decode controls
/// exclude that opener. The subscription leaves before the bus reconnects at end of body.
/// </remarks>
[MemoryDiagnoser]
public class PromptEventStreamBenchmarks : IDisposable
{
    private const int FrameCount = 16 * 65;
    private const int SocketChunkBytes = 1460;

    private byte[][] _frames = [];
    private JsonTypeInfo[] _concreteTypes = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;

    public static IEnumerable<WireFixture> Fixtures()
    {
        // Sixteen blocks of (text, text, reasoning, tool-input) x16, then text-ended: 64:1.
        // Each text-ended holds all 32 text deltas from its block.
        byte[][] payloads = [BenchmarkFixtures.ConnectedEventBody(), .. Frames().Select(static frame => Encoding.UTF8.GetBytes(frame.Body))];
        yield return new WireFixture("prompt-16x64deltas-1ended", BenchmarkFixtures.EventStream(payloads), FrameCount,
            payloadBytesPerItem: payloads.Skip(1).Sum(static payload => payload.Length) / FrameCount);
    }

    private static IEnumerable<(string Body, JsonTypeInfo Type)> Frames()
    {
        for (var index = 0; index < FrameCount; index++)
        {
            var id = "evt_bench" + (index + 1).ToString("D20", CultureInfo.InvariantCulture);
            yield return (index % 65) switch
            {
                64 => (BenchmarkFixtures.PromptTextEndedBody(id), OpenCodeJsonContext.Default.SessionTextEnded),
                var delta => (delta % 4) switch
                {
                    0 or 1 => (BenchmarkFixtures.PromptTextDeltaBody(id), OpenCodeJsonContext.Default.SessionTextDelta),
                    2 => (BenchmarkFixtures.PromptReasoningDeltaBody(id), OpenCodeJsonContext.Default.SessionReasoningDelta),
                    _ => (BenchmarkFixtures.PromptToolInputDeltaBody(id), OpenCodeJsonContext.Default.SessionToolInputDelta),
                },
            };
        }
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _concreteTypes = [.. Frames().Select(static frame => frame.Type)];
        var frames = new List<byte[]>();
        using var stream = new MemoryStream(Fixture.Bytes);
        await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
        {
            var expected = frames.Count == 0 ? typeof(EventServerConnected) : _concreteTypes[frames.Count - 1].Type;
            if (JsonSerializer.Deserialize(frame.Data.Span, OpenCodeJsonContext.Default.IEvent)?.GetType() != expected)
            {
                throw new InvalidOperationException("The prompt fixture did not dispatch to its expected concrete event type.");
            }

            frames.Add(frame.Data.ToArray());
        }

        _frames = [.. frames.Skip(1)];
        if (_frames.Length != FrameCount)
        {
            throw new InvalidOperationException("The prompt fixture did not contain its opener and 1040 turn frames.");
        }

        _handler = new CannedResponseHandler(Fixture.Bytes, "text/event-stream", suspendingChunkBytes: SocketChunkBytes);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, new OpenCodeClientOptions
        {
            Endpoint = new Uri("https://benchmark.invalid"),
            Password = "benchmark",
        });
        await RequireConnectionAsync().ConfigureAwait(false);
        if (_handler.LastBody is not { } body || body.SuspendedReads < body.MinimumReads)
        {
            throw new InvalidOperationException("The prompt subscription's socket-sized reads did not suspend.");
        }
    }

    /// <summary>The live subscription, including its opener, with every socket-sized read suspending.</summary>
    [Benchmark]
    public async Task<IEvent?> SubscribeSuspendingAsync()
    {
        IEvent? result = null;
        var count = 0;
        await foreach (var item in _client!.Events.SubscribeAsync().ConfigureAwait(false))
        {
            result = item;
            if (++count == FrameCount + 1)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Turn-frame materialization through the live-bus union, without SSE or the opener.</summary>
    [Benchmark]
    public IEvent? DeserializeFrames()
    {
        IEvent? result = null;
        foreach (var frame in _frames)
        {
            result = JsonSerializer.Deserialize(frame, OpenCodeJsonContext.Default.IEvent);
        }

        return result;
    }

    /// <summary>The same turn bodies decoded to setup-prepared concrete types, without union dispatch.</summary>
    [Benchmark]
    public object? DeserializeConcreteFrames()
    {
        object? result = null;
        for (var index = 0; index < _frames.Length; index++)
        {
            result = JsonSerializer.Deserialize(_frames[index], _concreteTypes[index]);
        }

        return result;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client?.Dispose();
            _httpClient?.Dispose();
            _handler?.Dispose();
        }
    }

    private async Task RequireConnectionAsync()
    {
        var count = 0;
        await foreach (var item in _client!.Events.SubscribeAsync().ConfigureAwait(false))
        {
            var expected = count == 0 ? typeof(EventServerConnected) : _concreteTypes[count - 1].Type;
            if (item.GetType() != expected)
            {
                throw new InvalidOperationException("The prompt subscription delivered an unexpected event type or order.");
            }

            if (++count == FrameCount + 1)
            {
                return;
            }
        }

        throw new InvalidOperationException("The prompt subscription ended before delivering its whole connection.");
    }
}
