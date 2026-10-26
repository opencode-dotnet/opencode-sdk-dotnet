using System.Net.Http.Headers;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// Route and query composition alone, per request shape: the two-parameter path route and the
/// query-bearing list route, plus production message construction and full request decoration
/// (authorization, location selection and user agent) with the harness's header reset.
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
    /// its directory and leaves its workspace unset, so the decoration below merges the directory: a set
    /// member wins (forcing the escape to recompute) over the ambient one.
    /// </summary>
    private static readonly LocationSelector AmbientLocation = new() { Directory = "/repo" };

    private static readonly LocationSelector PerCallLocation = new() { Directory = "/repo/worktree" };
    private static readonly ReadOnlyMemory<PipelinePolicy> Terminal = new PipelinePolicy[] { NoOpTerminalPolicy.Instance };

    private static readonly RequestDecorationPolicy Decoration = new(
        new AuthenticationHeaderValue("Basic", "YmVuY2g6YmVuY2g="),
        AmbientLocation,
        UserAgentPolicy.Resolve());

    private PipelineExchange? _exchange;
    private OperationRequest? _operation;
    private PipelineMessage? _message;

    [GlobalSetup]
    public void Setup()
    {
        var connection = new ConnectionSnapshot(EndpointBase, authorization: null, AmbientLocation, TimeProvider.System);
        _exchange = new PipelineExchange(connection, clientNetworkTimeout: null, Terminal, CancellationToken.None);
        _operation = OperationRequest.Describe<object>("session.message.get", HttpMethod.Get,
            OpenCodeRoutes.Sessions.GetMessage(SessionId, MessageId), body: null, bodyTypeInfo: null,
            content: null, new OpenCodeRequestOptions { Location = PerCallLocation }, declaredHeaders: null);
        _message = _exchange.CreateMessage(_operation, bufferBody: true, perCallNetworkTimeout: null, CancellationToken.None);
    }

    /// <summary>The two-parameter path route: segment concatenation plus value escaping.</summary>
    [Benchmark(Baseline = true)]
    public string PathRoute() => OpenCodeRoutes.Sessions.GetMessage(SessionId, MessageId);

    /// <summary>The query-bearing list route: builder, escaping, and final concatenation.</summary>
    [Benchmark]
    public string QueryRoute() =>
        OpenCodeRoutes.Sessions.ListMessages(SessionId, FullQuery);

    /// <summary>Production message creation over an operation described once in setup: URI, HTTP request, and pipeline message.</summary>
    [Benchmark]
    public Uri PathRouteRequestConstruction()
    {
        using var message = _exchange!.CreateMessage(_operation!, bufferBody: true, perCallNetworkTimeout: null, CancellationToken.None);

        return message.Request.RequestUri!;
    }

    /// <summary>
    /// A preconstructed path request, decorated through the same <see cref="RequestDecorationPolicy"/>
    /// every request rides, with a per-call location that merges over the ambient snapshot member
    /// by member (directory overridden and re-escaped), plus authorization and user-agent writes.
    /// Includes the harness's header reset each call so repeated decoration does not accumulate
    /// values; request construction is measured separately.
    /// An <c>async ValueTask</c> method whose awaits complete synchronously allocates no state
    /// machine box, so the rung carries no async wrapper cost over the no-op terminal.
    /// The reused message keeps its header container across calls (<c>Clear()</c> keeps its storage), so
    /// the rung excludes the header container's creation, which a fresh production request pays.
    /// </summary>
    [Benchmark]
    public async ValueTask<HttpRequestHeaders> PathRouteDecorationAndHeaderResetAsync()
    {
        var message = _message!;
        message.Request.Headers.Clear();
        await Decoration.ProcessAsync(message, Terminal).ConfigureAwait(false);
        return message.Request.Headers;
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
