using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The session-log stream decomposed per workload: the complete generated operation over a
/// canned event-stream response (request pipeline, SSE reader, generated stream adapter,
/// source-generated union dispatch), the reader plus per-frame materialization without HTTP,
/// and per-frame materialization alone over the exact frame strings the pipeline would
/// deserialize. The workloads cover many small watermarks, large long-string events, large
/// structured events whose content is nested union payload, and a realistic mix. The suspending
/// operation reads the same body in socket-sized reads that each suspend, as a live stream does, so
/// it adds cross-read assembly and the resumes the buffered operation never pays.
/// The raw-send rung sends and drains the same response with no SDK (<see cref="CannedExchange"/>),
/// the floor a buffered pipeline rung stands on; the suspending raw row is the suspending
/// operation's.
/// </summary>
[MemoryDiagnoser]
public class SessionLogStreamBenchmarks : IDisposable
{
    private const int LargeFrameCount = 64;
    private const int LargeTitleCharacters = 2048;
    private const int MixedFrameCount = 256;
    private const int MixedTitleCharacters = 512;
    private const int SmallFrameCount = 1024;
    private const int StructuredContentParts = 16;
    private const int SocketChunkBytes = 1460;

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _body = [];
    private string[] _frames = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private CannedExchange? _raw;
    private SessionClient? _session;
    private CannedResponseHandler? _suspendingHandler;
    private HttpClient? _suspendingHttpClient;
    private OpenCodeClient? _suspendingClient;
    private SessionClient? _suspendingSession;
    private CannedExchange? _suspendingRaw;

    public static IEnumerable<WireFixture> Fixtures()
    {
        yield return Frames("synced-x1024", BenchmarkFixtures.SessionLogSyncedBody(), SmallFrameCount);
        yield return Frames("created-2048-x64", BenchmarkFixtures.LargeSessionCreatedBody(LargeTitleCharacters), LargeFrameCount);
        yield return Frames("tool-success-16-x64", BenchmarkFixtures.SessionToolSuccessBody(StructuredContentParts), LargeFrameCount);
        yield return Mixed();
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _body = Fixture.Bytes;
        _handler = new CannedResponseHandler(_body, "text/event-stream");
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _raw = new CannedExchange(_httpClient, HttpMethod.Get, Options.Endpoint!);
        await _raw.RequireDrainAsync(_body.Length).ConfigureAwait(false);
        _session = _client.Sessions.GetSessionClient("ses_bench");
        _suspendingHandler = new CannedResponseHandler(_body, "text/event-stream", suspendingChunkBytes: SocketChunkBytes);
        _suspendingHttpClient = new HttpClient(_suspendingHandler);
        _suspendingClient = new OpenCodeClient(_suspendingHttpClient, Options);
        _suspendingSession = _suspendingClient.Sessions.GetSessionClient("ses_bench");
        _suspendingRaw = new CannedExchange(_suspendingHttpClient, HttpMethod.Get, Options.Endpoint!);
        await _suspendingRaw.RequireDrainAsync(_body.Length).ConfigureAwait(false);
        _frames = await CollectFramesAsync(_body).ConfigureAwait(false);

        var count = 0;
        await foreach (var item in _session.ReadLogAsync().ConfigureAwait(false))
        {
            if (item is UnknownSessionLogItem)
            {
                throw new InvalidOperationException("A stream fixture frame dispatched to the unknown carrier.");
            }

            count++;
        }

        if (count != Fixture.Items || _frames.Length != Fixture.Items)
        {
            throw new InvalidOperationException("The stream fixture did not materialize the expected frame count.");
        }

        var suspended = 0;
        await foreach (var item in _suspendingSession.ReadLogAsync().ConfigureAwait(false))
        {
            if (item is UnknownSessionLogItem)
            {
                throw new InvalidOperationException("A suspending stream frame dispatched to the unknown carrier.");
            }

            suspended++;
        }

        if (suspended != Fixture.Items || _suspendingHandler.LastBody is not { } body || body.SuspendedReads < body.MinimumReads)
        {
            throw new InvalidOperationException("The suspending stream operation did not read its frames over suspended reads.");
        }
    }

    /// <summary>The complete generated stream operation over one canned response.</summary>
    [Benchmark]
    public async Task<int> ReadLogAsync()
    {
        var session = _session!;
        var count = 0;
        await foreach (var item in session.ReadLogAsync().ConfigureAwait(false))
        {
            _ = item;
            count++;
        }

        return count;
    }

    /// <summary>The complete generated stream operation over a body whose every socket-sized read suspends.</summary>
    [Benchmark]
    public async Task<int> ReadLogSuspendingAsync()
    {
        var session = _suspendingSession!;
        var count = 0;
        await foreach (var item in session.ReadLogAsync().ConfigureAwait(false))
        {
            _ = item;
            count++;
        }

        return count;
    }

    /// <summary>The suspending harness floor: a bare send through the suspending handler with the body drained, no SDK.</summary>
    [Benchmark]
    public Task<int> SendRawSuspendingRequestAsync() => _suspendingRaw!.SendAndDrainAsync();

    /// <summary>Framing plus per-frame materialization without the request pipeline or HTTP.</summary>
    [Benchmark]
    public async Task<ISessionLogItem?> ReadFramesAndDeserializeAsync()
    {
        using var stream = new MemoryStream(_body);
        ISessionLogItem? result = null;
        await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
        {
            result = JsonSerializer.Deserialize(frame.Data.Span, OpenCodeJsonContext.Default.ISessionLogItem);
        }

        return result;
    }

    /// <summary>Per-frame materialization alone over the exact frame strings the pipeline deserializes.</summary>
    [Benchmark]
    public ISessionLogItem? DeserializeFrames()
    {
        ISessionLogItem? result = null;
        foreach (var frame in _frames)
        {
            result = JsonSerializer.Deserialize(frame, OpenCodeJsonContext.Default.ISessionLogItem);
        }

        return result;
    }

    /// <summary>The harness floor: a bare send through the same handler with the body drained, no SDK.</summary>
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

    private static WireFixture Frames(string name, byte[] payload, int count) =>
        new(name, BenchmarkFixtures.EventStream(payload, count), count, payloadBytesPerItem: payload.Length);

    private static WireFixture Mixed()
    {
        byte[][] cycle =
        [
            BenchmarkFixtures.LargeSessionCreatedBody(MixedTitleCharacters),
            BenchmarkFixtures.SessionLogSyncedBody(),
            BenchmarkFixtures.SessionToolSuccessBody(StructuredContentParts),
            BenchmarkFixtures.SessionDeletedBody(),
        ];
        var payloads = new byte[MixedFrameCount][];
        var payloadBytes = 0L;
        for (var index = 0; index < MixedFrameCount; index++)
        {
            payloads[index] = cycle[index % cycle.Length];
            payloadBytes += payloads[index].Length;
        }

        return new WireFixture("mixed-x256", BenchmarkFixtures.EventStream(payloads), MixedFrameCount,
            payloadBytesPerItem: (int)(payloadBytes / MixedFrameCount));
    }

    private static async Task<string[]> CollectFramesAsync(byte[] body)
    {
        using var stream = new MemoryStream(body);
        var frames = new List<string>();
        await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
        {
            frames.Add(Encoding.UTF8.GetString(frame.Data.Span));
        }

        return [.. frames];
    }
}
