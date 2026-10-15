using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The live event bus over a canned stream of the small idle events it carries most of: the
/// complete generated subscription through the wide <c>IEvent</c> union, per-frame
/// materialization alone over the exact frame strings the pipeline deserializes, and the
/// no-dispatch control materializing the same frames into the known record. The suspending
/// subscription reads the same body in socket-sized reads that each suspend, as a live bus does, so
/// it adds cross-read assembly and the resumes the buffered subscription never pays.
/// The raw-send rung is the harness alone (<see cref="CannedExchange"/>), so a pipeline rung minus
/// it is the SDK's share.
/// </summary>
[MemoryDiagnoser]
public class EventStreamBenchmarks : IDisposable
{
    private const int FrameCount = 1024;
    private const int SocketChunkBytes = 1460;

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

    public static IEnumerable<WireFixture> Fixtures()
    {
        var payload = BenchmarkFixtures.SessionIdleEventBody();
        yield return new WireFixture("idle-x1024", BenchmarkFixtures.EventStream(payload, FrameCount), FrameCount, payloadBytesPerItem: payload.Length);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

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

        var frames = new List<string>();
        using (var stream = new MemoryStream(Fixture.Bytes))
        {
            await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
            {
                frames.Add(frame.Data);
            }
        }

        _frames = [.. frames];
        var count = 0;
        await foreach (var item in _client.Events.SubscribeAsync().ConfigureAwait(false))
        {
            if (item is not SessionIdle)
            {
                throw new InvalidOperationException("An event fixture frame did not dispatch to the idle event.");
            }

            count++;
        }

        if (count != FrameCount || _frames.Length != FrameCount)
        {
            throw new InvalidOperationException("The event fixture did not materialize the expected frame count.");
        }

        if (await SubscribeSuspendingAsync().ConfigureAwait(false) != FrameCount
            || _suspendingHandler.LastBody is not { } body
            || body.SuspendedReads < Fixture.Bytes.Length / SocketChunkBytes)
        {
            throw new InvalidOperationException("The suspending subscription did not read its frames over suspended reads.");
        }
    }

    /// <summary>The complete generated subscription over one canned response.</summary>
    [Benchmark]
    public async Task<int> SubscribeAsync()
    {
        var client = _client!;
        var count = 0;
        await foreach (var item in client.Events.SubscribeAsync().ConfigureAwait(false))
        {
            _ = item;
            count++;
        }

        return count;
    }

    /// <summary>The complete generated subscription over a body whose every socket-sized read suspends.</summary>
    [Benchmark]
    public async Task<int> SubscribeSuspendingAsync()
    {
        var client = _suspendingClient!;
        var count = 0;
        await foreach (var item in client.Events.SubscribeAsync().ConfigureAwait(false))
        {
            _ = item;
            count++;
        }

        return count;
    }

    /// <summary>Per-frame materialization alone through the live-bus union.</summary>
    [Benchmark]
    public int DeserializeFrames()
    {
        var count = 0;
        foreach (var frame in _frames)
        {
            _ = JsonSerializer.Deserialize(frame, OpenCodeJsonContext.Default.IEvent);
            count++;
        }

        return count;
    }

    /// <summary>The control: the same frames materialized into the known record without union dispatch.</summary>
    [Benchmark]
    public int DeserializeConcreteFrames()
    {
        var count = 0;
        foreach (var frame in _frames)
        {
            _ = JsonSerializer.Deserialize(frame, OpenCodeJsonContext.Default.SessionIdle);
            count++;
        }

        return count;
    }

    /// <summary>The harness floor: the same canned handler through a bare <see cref="HttpClient"/> send with the body drained, no SDK.</summary>
    [Benchmark]
    public Task<int> SendRawRequestAsync() => _raw!.SendAndDrainAsync();

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
}
