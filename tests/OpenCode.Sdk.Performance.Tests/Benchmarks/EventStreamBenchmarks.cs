using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The live event bus over a canned connection of the small idle events it carries most of: the
/// complete generated subscription through the wide <c>IEvent</c> union, per-frame
/// materialization alone over the exact frame strings the pipeline deserializes, and the
/// no-dispatch control materializing the same frames into the known record. The suspending
/// subscription reads the same connection in socket-sized reads that each suspend, as a live bus
/// does, adding cross-read assembly and the resumes the buffered subscription never pays. The raw
/// rows send and drain the same responses with no SDK (<see cref="CannedExchange"/>).
/// </summary>
/// <remarks>
/// A connection opens with <c>server.connected</c>, as the server writes it, and the bus refuses
/// one that does not. The bus reconnects at the end of a body rather than ending, so each
/// subscription rung reads the connection's events and leaves, as a consumer does. Its
/// <c>Alloc B/item</c> includes one opener per 1024 idle events (about 0.1%); the fixture's item
/// count remains 1024 for the materialization rows, which exclude the opener.
/// </remarks>
[MemoryDiagnoser]
public class EventStreamBenchmarks : IDisposable
{
    private const int FrameCount = 1024;
    private const int EventsPerConnection = FrameCount + 1;
    private const int SocketChunkBytes = 1460;
    private const string ConnectedEvent = "{\"id\":\"evt_connected\",\"type\":\"server.connected\",\"data\":{}}";

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private string[] _frames = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private CannedExchange? _raw;
    private CannedResponseHandler? _suspendingHandler;
    private HttpClient? _suspendingHttpClient;
    private OpenCodeClient? _suspendingClient;
    private CannedExchange? _suspendingRaw;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var payload = BenchmarkFixtures.SessionIdleEventBody();
        var payloads = new byte[EventsPerConnection][];
        payloads[0] = Encoding.UTF8.GetBytes(ConnectedEvent);
        for (var index = 1; index < payloads.Length; index++)
        {
            payloads[index] = payload;
        }

        yield return new WireFixture("idle-x1024", BenchmarkFixtures.EventStream(payloads), FrameCount, payloadBytesPerItem: payload.Length);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    /// <summary>
    /// Proves both subscriptions deliver <c>server.connected</c> and then every idle event, the
    /// suspending one over reads that really suspended, and both raw rows drain the whole body.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _handler = new CannedResponseHandler(Fixture.Bytes, "text/event-stream");
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _raw = new CannedExchange(_httpClient, HttpMethod.Get, Options.Endpoint!);
        _suspendingHandler = new CannedResponseHandler(Fixture.Bytes, "text/event-stream", suspendingChunkBytes: SocketChunkBytes);
        _suspendingHttpClient = new HttpClient(_suspendingHandler);
        _suspendingClient = new OpenCodeClient(_suspendingHttpClient, Options);
        _suspendingRaw = new CannedExchange(_suspendingHttpClient, HttpMethod.Get, Options.Endpoint!);

        var frames = new List<string>();
        using (var stream = new MemoryStream(Fixture.Bytes))
        {
            await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
            {
                frames.Add(frame.Data);
            }
        }

        // The materialization rows read the idle events, not the connection's opening frame.
        _frames = [.. frames.Skip(1)];
        if (_frames.Length != FrameCount)
        {
            throw new InvalidOperationException("The event fixture did not frame into the connected event and its idle events.");
        }

        await RequireConnectionAsync(_client, "buffered").ConfigureAwait(false);
        await RequireConnectionAsync(_suspendingClient, "suspending").ConfigureAwait(false);
        if (_suspendingHandler.LastBody is not { } body || body.SuspendedReads < body.MinimumReads)
        {
            throw new InvalidOperationException("The suspending subscription's reads did not suspend.");
        }

        await _raw.RequireDrainAsync(Fixture.Bytes.Length).ConfigureAwait(false);
        await _suspendingRaw.RequireDrainAsync(Fixture.Bytes.Length).ConfigureAwait(false);
    }

    /// <summary>The complete generated subscription over one canned connection.</summary>
    [Benchmark]
    public Task<int> SubscribeAsync() => ReadConnectionAsync(_client!);

    /// <summary>The complete generated subscription over a connection whose every socket-sized read suspends.</summary>
    [Benchmark]
    public Task<int> SubscribeSuspendingAsync() => ReadConnectionAsync(_suspendingClient!);

    /// <summary>Per-frame materialization alone through the live-bus union.</summary>
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

    /// <summary>The control: the same frames materialized into the known record without union dispatch.</summary>
    [Benchmark]
    public SessionIdle? DeserializeConcreteFrames()
    {
        SessionIdle? result = null;
        foreach (var frame in _frames)
        {
            result = JsonSerializer.Deserialize(frame, OpenCodeJsonContext.Default.SessionIdle);
        }

        return result;
    }

    /// <summary>The buffered harness floor: a bare send through the same handler with the body drained, no SDK.</summary>
    [Benchmark]
    public Task<int> SendRawRequestAsync() => _raw!.SendAndDrainAsync();

    /// <summary>The suspending harness floor: the same, over the suspending body.</summary>
    [Benchmark]
    public Task<int> SendRawSuspendingRequestAsync() => _suspendingRaw!.SendAndDrainAsync();

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _suspendingClient?.Dispose();
        _suspendingHttpClient?.Dispose();
        _suspendingHandler?.Dispose();
        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    /// <summary>Reads one connection's events and leaves before the bus reconnects.</summary>
    private static async Task<int> ReadConnectionAsync(OpenCodeClient client)
    {
        var count = 0;
        await foreach (var item in client.Events.SubscribeAsync().ConfigureAwait(false))
        {
            _ = item;
            if (++count == EventsPerConnection)
            {
                break;
            }
        }

        return count;
    }

    private static async Task RequireConnectionAsync(OpenCodeClient client, string which)
    {
        var count = 0;
        await foreach (var item in client.Events.SubscribeAsync().ConfigureAwait(false))
        {
            var expected = count == 0 ? item is EventServerConnected : item is SessionIdle;
            if (!expected)
            {
                throw new InvalidOperationException($"The {which} subscription's event {count.ToString(CultureInfo.InvariantCulture)} was a {item.GetType().Name}.");
            }

            if (++count == EventsPerConnection)
            {
                return;
            }
        }

        throw new InvalidOperationException($"The {which} subscription ended after {count.ToString(CultureInfo.InvariantCulture)} of {EventsPerConnection.ToString(CultureInfo.InvariantCulture)} events.");
    }
}
