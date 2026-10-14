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
/// no-dispatch control materializing the same frames into the known record.
/// </summary>
[MemoryDiagnoser]
public class EventStreamBenchmarks : IDisposable
{
    private const int FrameCount = 1024;

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private string[] _frames = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;

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
}
