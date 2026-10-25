using System.Net.Http.Headers;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// Route and query composition alone, per request shape: the constant route as the
/// composition-free control, the two-parameter path route, and the query-bearing list route,
/// plus path request construction and a per-call location merge over an ambient snapshot —
/// the member-by-member merge <see cref="RequestDecorationPolicy"/> performs on every request
/// that carries a per-call <see cref="OpenCodeRequestOptions.Location"/>.
/// </summary>
[MemoryDiagnoser]
public class RouteCompositionBenchmarks : IDisposable
{
    private const string EndpointBase = "https://benchmark.invalid";
    private const string SessionId = "ses_bench0000000000000000001";
    private const string MessageId = "msg_bench0000000000000000001";

    private static readonly SessionMessageListRequest FullQuery = new()
    {
        Limit = 50,
        Order = ListOrder.Descending,
        Cursor = "cur_0123456789abcdef",
    };

    /// <summary>
    /// The ambient snapshot a client is constructed with. <see cref="PerCallLocation"/> overrides
    /// its directory and leaves its workspace unset, so the merge below exercises both member
    /// rules at once: a set member wins (directory, forcing the escape to recompute) over the ambient directory.
    /// </summary>
    private static readonly LocationSelector AmbientLocation = new() { Directory = "/repo" };

    private static readonly LocationSelector PerCallLocation = new() { Directory = "/repo/worktree" };
    private static readonly ReadOnlyMemory<PipelinePolicy> Terminal = new PipelinePolicy[] { NoOpTerminalPolicy.Instance };

    private static readonly RequestDecorationPolicy Decoration = new(
        new AuthenticationHeaderValue("Basic", "YmVuY2g6YmVuY2g="),
        AmbientLocation,
        UserAgentPolicy.Resolve());

    private string _constantRoute = string.Empty;
    private string _pathRoute = string.Empty;
    private PipelineMessage? _message;

    [GlobalSetup]
    public void Setup()
    {
        _constantRoute = OpenCodeRoutes.Server.GetInfo;
        _pathRoute = OpenCodeRoutes.Sessions.GetMessage(SessionId, MessageId);
        _message = new PipelineMessage
        {
            NetworkTimeout = NetworkTimeouts.DefaultNetworkTimeout,
            Request = new HttpRequestMessage(HttpMethod.Get, new Uri(EndpointBase + _pathRoute, UriKind.Absolute)),
            PerCallLocation = PerCallLocation,
        };
    }

    /// <summary>The constant no-parameter route: the call-overhead floor.</summary>
    [Benchmark]
    public string ConstantRoute() => _constantRoute;

    /// <summary>The two-parameter path route: segment concatenation plus value escaping.</summary>
    [Benchmark(Baseline = true)]
    public string PathRoute() => OpenCodeRoutes.Sessions.GetMessage(SessionId, MessageId);

    /// <summary>The query-bearing list route: builder, escaping, and final concatenation.</summary>
    [Benchmark]
    public string QueryRoute() =>
        OpenCodeRoutes.Sessions.ListMessages(SessionId, FullQuery);

    /// <summary>Request construction over a precomposed path route: URI, HTTP request, and pipeline message.</summary>
    [Benchmark]
    public Uri PathRouteRequestConstruction()
    {
        using var message = new PipelineMessage
        {
            NetworkTimeout = NetworkTimeouts.DefaultNetworkTimeout,
            Request = new HttpRequestMessage(HttpMethod.Get, new Uri(EndpointBase + _pathRoute, UriKind.Absolute)),
            PerCallLocation = PerCallLocation,
        };

        return message.Request.RequestUri!;
    }

    /// <summary>
    /// A preconstructed path request, decorated through the same <see cref="RequestDecorationPolicy"/>
    /// every request rides, with a per-call location that merges over the ambient snapshot member
    /// by member (directory overridden and re-escaped). Includes clearing headers each call so
    /// repeated decoration does not accumulate values; request construction is measured separately.
    /// An <c>async ValueTask</c> method whose awaits complete synchronously allocates no state
    /// machine box, so the rung carries no async wrapper cost over the no-op terminal.
    /// </summary>
    [Benchmark]
    public async ValueTask<string> PathRouteWithLocationMergeAsync()
    {
        var message = _message!;
        message.Request.Headers.Clear();
        await Decoration.ProcessAsync(message, Terminal).ConfigureAwait(false);
        return message.Request.Headers.GetValues("x-opencode-directory").First();
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

        _message?.Dispose();
    }
}
