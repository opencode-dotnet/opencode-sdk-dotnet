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
/// Wire bytes include the whole connection; items count only turn frames. Each subscription
/// row's Alloc B/item includes one server.connected opener per connection. Decode controls
/// exclude that opener. The subscription leaves before the bus reconnects at end of body.
/// </remarks>
[MemoryDiagnoser]
public class PromptEventStreamBenchmarks : IDisposable
{
    private const int FrameCount = 65;
    private const int SocketChunkBytes = 1460;
    private const string ConnectedEvent = "{\"id\":\"evt_connected\",\"type\":\"server.connected\",\"data\":{}}";
    private const string TextFragment =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private string[] _frames = [];
    private JsonTypeInfo[] _concreteTypes = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var text = "{\"id\":\"evt_text\",\"created\":1,\"type\":\"session.text.delta\",\"data\":{\"sessionID\":\"ses_bench\",\"assistantMessageID\":\"msg_bench\",\"ordinal\":0,\"delta\":\""
            + TextFragment + "\"}}";
        var reasoning = "{\"id\":\"evt_reason\",\"created\":1,\"type\":\"session.reasoning.delta\",\"data\":{\"sessionID\":\"ses_bench\",\"assistantMessageID\":\"msg_bench\",\"ordinal\":1,\"delta\":\"Thinking\"}}";
        var tool = "{\"id\":\"evt_tool\",\"created\":1,\"type\":\"session.tool.input.delta\",\"data\":{\"sessionID\":\"ses_bench\",\"assistantMessageID\":\"msg_bench\",\"id\":\"tool_bench\",\"delta\":\"x\"}}";
        var ended = "{\"id\":\"evt_ended\",\"created\":1,\"type\":\"session.text.ended\",\"durable\":{\"aggregateID\":\"ses_bench\",\"seq\":1,\"version\":1},\"data\":{\"sessionID\":\"ses_bench\",\"assistantMessageID\":\"msg_bench\",\"ordinal\":0,\"text\":\""
            + string.Concat(Enumerable.Repeat(TextFragment, 32)) + "\"}}";

        // Fixed order: (text, text, reasoning, tool-input) x16, then text-ended: 64:1.
        // 65 turn frames + 1 opener; text-ended holds all 32 text deltas (4096 ASCII bytes).
        // JSON body sizes (UTF-8): opener 58, text 274, reasoning 161, tool 159, ended 4300 B.
        var cycle = new[] { text, text, reasoning, tool };
        var payloads = new byte[FrameCount + 1][];
        payloads[0] = Encoding.UTF8.GetBytes(ConnectedEvent);
        for (var index = 0; index < FrameCount; index++)
        {
            payloads[index + 1] = Encoding.UTF8.GetBytes(index == FrameCount - 1 ? ended : cycle[index % cycle.Length]);
        }

        yield return new WireFixture("prompt-64deltas-1ended", BenchmarkFixtures.EventStream(payloads), FrameCount,
            payloadBytesPerItem: payloads.Skip(1).Sum(static payload => payload.Length) / FrameCount);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        JsonTypeInfo[] cycle =
        [
            OpenCodeJsonContext.Default.SessionTextDelta, OpenCodeJsonContext.Default.SessionTextDelta,
            OpenCodeJsonContext.Default.SessionReasoningDelta, OpenCodeJsonContext.Default.SessionToolInputDelta,
        ];
        _concreteTypes = [.. Enumerable.Range(0, FrameCount).Select(index =>
            index == FrameCount - 1 ? OpenCodeJsonContext.Default.SessionTextEnded : cycle[index % cycle.Length])];
        var frames = new List<string>();
        using var stream = new MemoryStream(Fixture.Bytes);
        await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
        {
            var expected = frames.Count == 0 ? typeof(EventServerConnected) : _concreteTypes[frames.Count - 1].Type;
            if (JsonSerializer.Deserialize(frame.Data, OpenCodeJsonContext.Default.IEvent)?.GetType() != expected)
            {
                throw new InvalidOperationException("The prompt fixture did not dispatch to its expected concrete event type.");
            }

            frames.Add(frame.Data);
        }

        _frames = [.. frames.Skip(1)];
        if (_frames.Length != FrameCount)
        {
            throw new InvalidOperationException("The prompt fixture did not contain its opener and 65 turn frames.");
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
