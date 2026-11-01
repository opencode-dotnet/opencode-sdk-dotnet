using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>A string-heavy one-shot diff, decomposed from the complete call to the raw-send floor.</summary>
[MemoryDiagnoser]
public class SessionDiffBenchmarks : IDisposable
{
    private const string SessionId = "ses_bench0000000000000000001";
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _envelope = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private CannedExchange? _raw;
    private SessionClient? _session;
    private Pipeline? _pipeline;

    public static IEnumerable<WireFixture> Fixtures()
    {
        // 17,909,180, 35,929,182 and 71,969,182 bytes: under and over the downlevel response pool's 64 MiB ceiling.
        yield return Envelope("diff-17m", 170_000);
        yield return Envelope("diff-35m", 340_000);
        yield return Envelope("diff-70m", 680_000);
        // UTF-8 body sizes after JSON escaping and the nine-byte data envelope: 4,550 and 1,249,176.
        // The large body is 19.13% above the downlevel shared pool's 1,048,576-byte boundary.
        yield return Envelope("diff-4k", 42);
        yield return Envelope("diff-1200k", 12000);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _envelope = Fixture.Bytes;
        _handler = new CannedResponseHandler(_envelope);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _raw = new CannedExchange(_httpClient, HttpMethod.Get, Options.Endpoint!);
        await _raw.RequireDrainAsync(_envelope.Length).ConfigureAwait(false);
        _session = _client.Sessions.GetSessionClient(SessionId);
        _pipeline = new Pipeline(_httpClient, ownsHttpClient: false, Options);

        var response = await GetDiffAsync().ConfigureAwait(false);
        RequireDiff(response);
        RequireDiff(AdaptSuccess());
        if (DeserializeEnvelope() is not SessionDiffResponseEnvelope { Data.Count: 1 })
        {
            throw new InvalidOperationException("The diff envelope did not materialize one file.");
        }

        if ((await ExecuteWithoutAdapterAsync().ConfigureAwait(false)).Status != 200)
        {
            throw new InvalidOperationException("The diff pipeline did not return status 200.");
        }
    }

    /// <summary>The complete generated diff operation.</summary>
    [Benchmark]
    public Task<SessionDiffResponse> GetDiffAsync() => _session!.GetDiffAsync();

    /// <summary>The buffered pipeline without payload materialization.</summary>
    [Benchmark]
    public Task<NoOpResponse> ExecuteWithoutAdapterAsync() =>
        _pipeline!.ExecuteAsync("session.diff", HttpMethod.Get, OpenCodeRoutes.Sessions.GetDiff(SessionId), NoOpResponseAdapter.Instance, options: null, CancellationToken.None);

    /// <summary>The generated adapter over validated UTF-8.</summary>
    [Benchmark]
    public SessionDiffResponse AdaptSuccess() => SessionDiffResponseAdapter.Instance.AdaptSuccess(200, _envelope);

    /// <summary>Source-generated materialization of the diff envelope alone.</summary>
    [Benchmark]
    public object? DeserializeEnvelope() => JsonSerializer.Deserialize(_envelope, OpenCodeJsonContext.Default.SessionDiffResponseEnvelope);

    /// <summary>A bare send through the same handler with the body drained, no SDK.</summary>
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

        _pipeline?.Dispose();
        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    private static void RequireDiff(SessionDiffResponse response)
    {
        if (response.Diffs is not [{ File: "src/settings.ts", Patch.Length: > 4000 } diff])
        {
            throw new InvalidOperationException("The diff fixture did not materialize one source patch.");
        }

        var lines = diff.Patch.Split('\n').Skip(5).ToArray();
        if (diff.Additions != lines.Count(line => line.Length > 0 && line[0] == '+')
            || diff.Deletions != lines.Count(line => line.Length > 0 && line[0] == '-'))
        {
            throw new InvalidOperationException("The diff counts did not match the fixture's added and removed lines.");
        }
    }

    private static WireFixture Envelope(string name, int blocks)
    {
        var payload = BenchmarkFixtures.SessionDiffBody(blocks);
        return new WireFixture(name, BenchmarkFixtures.DataEnvelope(payload), items: 1, payloadBytesPerItem: payload.Length);
    }
}
