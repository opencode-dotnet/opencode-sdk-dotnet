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
/// structured events whose content is nested union payload, and a realistic mix.
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
    private SessionClient? _session;

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
        _session = _client.Sessions.GetSessionClient("ses_bench");
        _frames = await CollectFramesAsync(_body).ConfigureAwait(false);

        var count = 0;
        await foreach (var item in _session.GetLogAsync().ConfigureAwait(false))
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
    }

    /// <summary>The complete generated stream operation over one canned response.</summary>
    [Benchmark]
    public async Task<int> GetLogAsync()
    {
        var session = _session!;
        var count = 0;
        await foreach (var item in session.GetLogAsync().ConfigureAwait(false))
        {
            _ = item;
            count++;
        }

        return count;
    }

    /// <summary>Framing plus per-frame materialization without the request pipeline or HTTP.</summary>
    [Benchmark]
    public async Task<int> ReadFramesAndDeserializeAsync()
    {
        using var stream = new MemoryStream(_body);
        var count = 0;
        await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
        {
            _ = JsonSerializer.Deserialize(frame.Data, OpenCodeJsonContext.Default.ISessionLogItem);
            count++;
        }

        return count;
    }

    /// <summary>Per-frame materialization alone over the exact frame strings the pipeline deserializes.</summary>
    [Benchmark]
    public int DeserializeFrames()
    {
        var count = 0;
        foreach (var frame in _frames)
        {
            _ = JsonSerializer.Deserialize(frame, OpenCodeJsonContext.Default.ISessionLogItem);
            count++;
        }

        return count;
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
        if (!disposing)
        {
            return;
        }

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
            frames.Add(frame.Data);
        }

        return [.. frames];
    }
}
