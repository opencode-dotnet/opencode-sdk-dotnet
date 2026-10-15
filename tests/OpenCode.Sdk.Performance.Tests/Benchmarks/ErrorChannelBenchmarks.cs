using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The declared API-error channel over a canned 404: the <c>NoThrow</c> envelope, the default
/// throwing channel, and the tolerant typed-error reader alone. Raw-body retention on errors is
/// a contract (ADR-0007), so these rows baseline that contract's cost rather than hunt for waste.
/// The raw-send rung is the harness alone (<see cref="CannedExchange"/>), so a pipeline rung minus
/// it is the SDK's share.
/// </summary>
[MemoryDiagnoser]
public class ErrorChannelBenchmarks : IDisposable
{
    private static readonly string[] NotFoundTags = ["SessionNotFoundError"];
    private static readonly OpenCodeRequestOptions NoThrow = new()
    {
        ErrorBehavior = ErrorBehavior.NoThrow,
    };

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private string _rawBody = string.Empty;
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private CannedExchange? _raw;
    private SessionClient? _session;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var body = BenchmarkFixtures.SessionNotFoundErrorBody();
        yield return new WireFixture("not-found-404", body, items: 1, payloadBytesPerItem: body.Length);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _rawBody = Encoding.UTF8.GetString(Fixture.Bytes);
        _handler = new CannedResponseHandler(Fixture.Bytes, status: HttpStatusCode.NotFound);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _raw = new CannedExchange(_httpClient, HttpMethod.Get, Options.Endpoint!);
        _session = _client.Sessions.GetSessionClient("ses_bench0000000000000000001");

        var response = await GetSessionNoThrowAsync().ConfigureAwait(false);
        if (response is not { IsError: true, Error: SessionNotFoundError, RawBody: not null } || await GetSessionThrowingAsync().ConfigureAwait(false) is not 404)
        {
            throw new InvalidOperationException("The error fixture did not travel the declared 404 channel.");
        }
    }

    /// <summary>The error envelope on the response spine: typed error plus retained raw body, no exception.</summary>
    [Benchmark]
    public Task<SessionResponse> GetSessionNoThrowAsync() => _session!.GetAsync(NoThrow);

    /// <summary>The default channel: the same error thrown as <see cref="OpenCodeApiException"/> and caught.</summary>
    [Benchmark]
    public async Task<int> GetSessionThrowingAsync()
    {
        var session = _session!;
        try
        {
            _ = await session.GetAsync().ConfigureAwait(false);
            return 0;
        }
        catch (OpenCodeApiException exception)
        {
            return exception.Status;
        }
    }

    /// <summary>The tolerant typed-error reader alone over the decoded body.</summary>
    [Benchmark]
    public IOpenCodeError? ReadTolerantError() => OpenCodeErrorReader.Read(_rawBody, NotFoundTags);

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

        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }
}
