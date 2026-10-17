using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.Tests.Transport.Support;

using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Transport;

/// <summary>
/// Exercises <c>RequestDecorationPolicy</c>'s per-call/ambient location merge and its
/// document-declared header channel through a real <see cref="Pipeline"/> and a
/// <see cref="RecordingHttpHandler"/>, asserting the headers actually sent on the wire.
/// </summary>
public sealed class RequestDecorationPolicyTests
{
    private const string OperationId = "test.operation";

    private static async Task AssertDecoratedLocationAsync(
        Pipeline pipeline, RecordingHttpHandler handler, OpenCodeRequestOptions options, string expectedDirectory)
    {
        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = handler.Requests.Single();
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo(expectedDirectory);
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Let_PerCall_Location_Win_Member_By_Member()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector
        {
            Directory = "/amb/dir"
        });
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector { Directory = "/per/dir" },
        };

        await AssertDecoratedLocationAsync(pipeline, handler, options, "%2Fper%2Fdir");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Not_Clear_An_Ambient_Member_When_PerCall_Leaves_It_Null()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector
        {
            Directory = "/amb/dir"
        });

        // LocationSelector refuses blank members, so the only spelling of "clear" is null —
        // and null inherits. A per-call selector with directory left null must not clear
        // the ambient header.
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector(),
        };

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = handler.Requests.Single();
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Famb%2Fdir");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_PercentEncode_The_Directory()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector
            {
                Directory = "/tmp/päth ü"
            },
        };

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = handler.Requests.Single();
        // Pinned literal for Uri.EscapeDataString("/tmp/päth ü"), computed once rather than
        // recomputed inline so the test does not exercise the very API it is pinning.
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Ftmp%2Fp%C3%A4th%20%C3%BC");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_No_Location_Headers_When_Location_Is_Absent()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        var request = handler.Requests.Single();
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-directory");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_Declared_Headers_Alongside_The_Location_Headers()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector { Directory = "/amb/dir" });

        // The policy is told a name and a value and nothing else; it must write whatever it is
        // handed without recognizing the operation or the header.
        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Post,
            "/api/info",
            new RecordingResponseAdapter(),
            options: null,
            [new DeclaredHeader("x-probe-one", "first"), new DeclaredHeader("x-probe-two", "second")],
            CancellationToken.None);

        var request = handler.Requests.Single();
        await Assert.That(request.Headers["x-probe-one"]).IsEqualTo("first");
        await Assert.That(request.Headers["x-probe-two"]).IsEqualTo("second");
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Famb%2Fdir");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_No_Extra_Headers_When_Declared_Headers_Are_Absent()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Post,
            "/api/info",
            new RecordingResponseAdapter(),
            options: null,
            declaredHeaders: null,
            CancellationToken.None);

        var request = handler.Requests.Single();
        await Assert.That(request.Headers.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(["User-Agent"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_Only_The_Set_PerCall_Member_When_Ambient_Is_Absent()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector { Directory = "/per/dir" },
        };

        await AssertDecoratedLocationAsync(pipeline, handler, options, "%2Fper%2Fdir");
    }

    /// <summary>
    /// Pins <see cref="LocationMerge"/>'s query-channel merge and <c>RequestDecorationPolicy</c>'s
    /// header-channel merge to the same result for the same inputs, across a matrix covering
    /// directories set on both sides, only one side set, and neither set. The two sites restate the
    /// same member-by-member rule independently (<c>client-runtime.md</c>'s "Location merge"
    /// note); this test is what keeps a silent drift between them from going unnoticed.
    /// </summary>
    [Test]
    [Arguments("/per/dir", "/amb/dir")]
    [Arguments(null, "/amb/dir")]
    [Arguments("/per/dir", null)]
    [Arguments(null, null)]
    [Arguments("/tmp/päth ü", "/amb/dir")]
    public async Task LocationMerge_Should_Equal_What_RequestDecorationPolicy_Puts_On_The_Wire(
        string? perCallDirectory, string? ambientDirectory)
    {
        var perCall = perCallDirectory is null ? null : new LocationSelector { Directory = perCallDirectory };
        var ambient = ambientDirectory is null ? null : new LocationSelector { Directory = ambientDirectory };
        var expected = LocationMerge.Resolve(perCall, ambient);

        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: ambient);
        var options = perCall is null ? null : new OpenCodeRequestOptions { Location = perCall };

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = handler.Requests.Single();
        var wireDirectory = request.Headers.TryGetValue("x-opencode-directory", out var directoryHeader)
            ? Uri.UnescapeDataString(directoryHeader)
            : null;
        await Assert.That(wireDirectory).IsEqualTo(expected?.Directory);
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }
}
