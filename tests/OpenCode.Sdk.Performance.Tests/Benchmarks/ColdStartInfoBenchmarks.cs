using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>The server-info component ladder on each process's first call, including client construction.</summary>
/// <remarks>
/// Rows report first-use time only. The allocation column is not a first-use figure because
/// BenchmarkDotNet measures allocation in a later, warm invocation.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.ColdStart, launchCount: 5, warmupCount: 0, iterationCount: 1)]
public class ColdStartInfoBenchmarks
{
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _body = [];
    private ServerInfoResponse? _complete;
    private NoOpResponse? _pipelineResult;
    private ServerInfoResponse? _adapted;
    private ServerInfo? _materialized;

    [GlobalSetup]
    public void Setup() => _body = BenchmarkFixtures.InfoBody();

    /// <summary>The first complete operation on a freshly constructed client.</summary>
    [Benchmark]
    public async Task<ServerInfoResponse> GetInfoAsync()
    {
        using var handler = new CannedResponseHandler(_body);
        using var httpClient = new HttpClient(handler);
        using var client = new OpenCodeClient(httpClient, Options);
        return _complete = await client.Server.GetInfoAsync().ConfigureAwait(false);
    }

    /// <summary>The first pipeline execution without JSON materialization.</summary>
    [Benchmark]
    public async Task<NoOpResponse> ExecuteWithoutAdapterAsync()
    {
        using var handler = new CannedResponseHandler(_body);
        using var httpClient = new HttpClient(handler);
        using var pipeline = new Pipeline(httpClient, ownsHttpClient: false, Options);
        return _pipelineResult = await pipeline.ExecuteAsync("server.info", HttpMethod.Get, OpenCodeRoutes.Server.GetInfo,
            NoOpResponseAdapter.Instance, options: null, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The first generated adapter call over the canned UTF-8 body.</summary>
    [Benchmark]
    public ServerInfoResponse AdaptSuccess() => _adapted = ServerInfoResponseAdapter.Instance.AdaptSuccess(200, _body);

    /// <summary>The first source-generated materialization of the canned body.</summary>
    [Benchmark]
    public ServerInfo? Deserialize() => _materialized = JsonSerializer.Deserialize(_body, OpenCodeJsonContext.Default.ServerInfo);

    [IterationCleanup(Targets = new[] { nameof(GetInfoAsync) })]
    public void CheckComplete() => RequireIdentity(_complete?.ServerInfo);

    [IterationCleanup(Targets = new[] { nameof(ExecuteWithoutAdapterAsync) })]
    public void CheckPipeline()
    {
        if (_pipelineResult is not { Status: 200 })
        {
            throw new InvalidOperationException("The cold info pipeline did not return status 200.");
        }
    }

    [IterationCleanup(Targets = new[] { nameof(AdaptSuccess) })]
    public void CheckAdapter() => RequireIdentity(_adapted?.ServerInfo);

    [IterationCleanup(Targets = new[] { nameof(Deserialize) })]
    public void CheckMaterialization() => RequireIdentity(_materialized);

    private static void RequireIdentity(ServerInfo? info)
    {
        if (info is not { Version: "0.0.0-bench", Pid: 42 })
        {
            throw new InvalidOperationException("The cold info fixture did not materialize the expected server identity.");
        }
    }
}
