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

    private static async Task AssertHeaderAsync(IReadOnlyDictionary<string, string> headers, string name, string expected)
    {
        await Assert.That(headers).ContainsKey(name);
        await Assert.That(headers[name]).IsEqualTo(expected);
    }

    [Test]
    public async Task Headers_Should_Reach_Every_Cursor_Page()
    {
        using var scenario = ContractScenario.RespondingWithPages(WireBodyData.Page("", next: "continuation"), WireBodyData.Page(""));
        var options = new OpenCodeRequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Page"] = "every-page" },
        };
        var received = new List<string?>();
        await foreach (var page in scenario.Client.Sessions.GetSessionClient("ses_headers")
            .EnumerateMessagesAsync(requestOptions: options).Pages.WithCancellation(CancellationToken.None))
        {
            received.Add(page.Cursor.Next);
        }

        await Assert.That(received).IsEquivalentTo(["continuation", null], CollectionOrdering.Matching);
        await Assert.That(scenario.Requests.Count).IsEqualTo(2);
        foreach (var request in scenario.Requests)
        {
            await AssertHeaderAsync(request.Headers, "X-Page", "every-page");
        }
    }

    [Test]
    public async Task Headers_Should_Join_Case_Equivalent_Call_Names_In_Insertion_Order()
    {
        using var scenario = PipelineScenario.Responding();
        var call = new OpenCodeRequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["x-PROBE"] = "first", ["X-Probe"] = "second" },
        };
        _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), call, CancellationToken.None);

        await AssertHeaderAsync(scenario.Requests.Single().Headers, "X-Probe", "first, second");
    }

    [Test]
    [Arguments("Authorization", false, "Bearer caller")]
    [Arguments("x-opencode-directory", false, "/caller")]
    [Arguments("x-opencode-directory", true, "%2Fper-call")]
    public async Task Headers_Should_Accept_Unopposed_Client_Headers_And_Apply_PerCall_Location(string name, bool perCall, string expected)
    {
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);
        var options = new OpenCodeClientOptions { Endpoint = PipelineFactory.Endpoint, Username = "custom" };
        options.Headers.Add(name, name is "Authorization" ? "Bearer caller" : "/caller");
        using var pipeline = Pipeline.Create(http, options);
        options.Headers.Clear();
        options.Headers.Add("X-Added-Later", "mutated");
        var call = perCall ? new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/per-call" } } : null;

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), call, CancellationToken.None);

        await AssertHeaderAsync(handler.Requests.Single().Headers, name, expected);
        await Assert.That(handler.Requests.Single().Headers).DoesNotContainKey("X-Added-Later");
    }

    [Test]
    public async Task Headers_Should_Replace_Sdk_Credentials_And_Location_Per_Call()
    {
        using var scenario = PipelineScenario.Decorating(password: "secret", location: new LocationSelector { Directory = "/sdk" });
        var call = new OpenCodeRequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aUtHoRiZaTiOn"] = "Bearer call",
                ["X-OPENCODE-DIRECTORY"] = "/call",
            },
        };
        _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), call, CancellationToken.None);

        await Assert.That(scenario.Requests.Single().Authorization).IsEqualTo("Bearer call");
        await AssertHeaderAsync(scenario.Requests.Single().Headers, "x-opencode-directory", "/call");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Headers_Should_Replace_Content_Headers_Only_When_Content_Exists(bool perCall, bool hasBody)
    {
        string? language = null;
        using var handler = new RecordingHttpHandler(request =>
        {
            language = request.Content?.Headers.ContentLanguage.SingleOrDefault();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"] = "application/custom",
            ["Content-Language"] = "tr",
        };
        var client = new OpenCodeClientOptions { Endpoint = PipelineFactory.Endpoint };
        if (!perCall)
        {
            foreach (var header in headers)
            {
                client.Headers.Add(header);
            }
        }

        using var pipeline = Pipeline.Create(http, client);
        var call = perCall ? new OpenCodeRequestOptions { Headers = headers } : null;
        if (hasBody)
        {
            _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Post, "/api/info", new TestBody { Value = "probe" },
                TestBodyJsonContext.Default.TestBody, new RecordingResponseAdapter(), call, CancellationToken.None);
        }
        else
        {
            _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), call, CancellationToken.None);
        }

        var request = handler.Requests.Single();
        await Assert.That(request.ContentType).IsEqualTo(hasBody ? "application/custom" : null);
        await Assert.That(language).IsEqualTo(hasBody ? "tr" : null);
        await Assert.That(request.Headers).DoesNotContainKey("Content-Type");
        await Assert.That(request.Headers).DoesNotContainKey("Content-Language");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Headers_Should_Replace_Earlier_Levels_By_Name(int level)
    {
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);
        var clientOptions = new OpenCodeClientOptions { Endpoint = PipelineFactory.Endpoint };
        if (level >= 1)
        {
            clientOptions.Headers.Add("USER-AGENT", "client");
        }

        using var pipeline = Pipeline.Create(http, clientOptions);
        var call = new OpenCodeRequestOptions { Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["user-agent"] = "call" } };
        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(),
            level >= 3 ? call : null, level >= 2 ? [new DeclaredHeader("User-Agent", "declared")] : null, CancellationToken.None);

        var expected = level switch { 3 => "call", 2 => "declared", 1 => "client", _ => UserAgentPolicy.Resolve().ToString() };
        await AssertHeaderAsync(handler.Requests.Single().Headers, "User-Agent", expected);
    }

    [Test]
    public async Task A_Declared_Content_Header_Name_Should_Not_Fail_Decoration()
    {
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);
        using var pipeline = Pipeline.Create(http, new OpenCodeClientOptions { Endpoint = PipelineFactory.Endpoint });

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(),
            null, [new DeclaredHeader("Content-Language", "declared")], CancellationToken.None);

        await Assert.That(handler.Requests.Single().Headers).DoesNotContainKey("Content-Language");
    }

    private static async Task AssertDecoratedLocationAsync(
        Pipeline pipeline, IReadOnlyList<RecordedRequest> requests, OpenCodeRequestOptions options, string expectedDirectory)
    {
        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = requests.Single();
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo(expectedDirectory);
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Let_PerCall_Location_Win_Member_By_Member()
    {
        using var scenario = PipelineScenario.Decorating(location: new LocationSelector
        {
            Directory = "/amb/dir"
        });
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector { Directory = "/per/dir" },
        };

        await AssertDecoratedLocationAsync(scenario.Pipeline, scenario.Requests, options, "%2Fper%2Fdir");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Not_Clear_An_Ambient_Member_When_PerCall_Leaves_It_Null()
    {
        using var scenario = PipelineScenario.Decorating(location: new LocationSelector
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

        _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = scenario.Requests.Single();
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Famb%2Fdir");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_PercentEncode_The_Directory()
    {
        using var scenario = PipelineScenario.Responding();
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector
            {
                Directory = "/tmp/päth ü"
            },
        };

        _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = scenario.Requests.Single();
        // Pinned literal for Uri.EscapeDataString("/tmp/päth ü"), computed once rather than
        // recomputed inline so the test does not exercise the very API it is pinning.
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Ftmp%2Fp%C3%A4th%20%C3%BC");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_No_Location_Headers_When_Location_Is_Absent()
    {
        using var scenario = PipelineScenario.Responding();

        _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        var request = scenario.Requests.Single();
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-directory");
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_Declared_Headers_Alongside_The_Location_Headers()
    {
        using var scenario = PipelineScenario.Decorating(location: new LocationSelector { Directory = "/amb/dir" });

        // The policy is told a name and a value and nothing else; it must write whatever it is
        // handed without recognizing the operation or the header.
        _ = await scenario.Pipeline.ExecuteAsync(OperationId,
            HttpMethod.Post,
            "/api/info",
            new RecordingResponseAdapter(),
            options: null,
            [new DeclaredHeader("x-probe-one", "first"), new DeclaredHeader("x-probe-two", "second")],
            CancellationToken.None);

        var request = scenario.Requests.Single();
        await Assert.That(request.Headers["x-probe-one"]).IsEqualTo("first");
        await Assert.That(request.Headers["x-probe-two"]).IsEqualTo("second");
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Famb%2Fdir");
    }

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_No_Extra_Headers_When_Declared_Headers_Are_Absent()
    {
        using var scenario = PipelineScenario.Responding();

        _ = await scenario.Pipeline.ExecuteAsync(OperationId,
            HttpMethod.Post,
            "/api/info",
            new RecordingResponseAdapter(),
            options: null,
            declaredHeaders: null,
            CancellationToken.None);

        var request = scenario.Requests.Single();
        await Assert.That(request.Headers.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(["User-Agent"], CollectionOrdering.Matching);
    }

#if NET
    [Test]
    public async Task RequestDecorationPolicy_Should_Send_The_Product_Token_Unchanged_On_The_Wire()
    {
        using var scenario = PipelineScenario.Responding();

        _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        // The value is added unvalidated, so the unvalidated view is what the handler writes; the checked view would
        // reparse and normalize it. .NET Framework has no unvalidated view, so this row exists on .NET only.
        await Assert.That(scenario.Requests.Single().RawUserAgent).IsEqualTo(UserAgentPolicy.Resolve().ToString());
    }
#endif

    [Test]
    public async Task RequestDecorationPolicy_Should_Send_Only_The_Set_PerCall_Member_When_Ambient_Is_Absent()
    {
        using var scenario = PipelineScenario.Responding();
        var options = new OpenCodeRequestOptions
        {
            Location = new LocationSelector { Directory = "/per/dir" },
        };

        await AssertDecoratedLocationAsync(scenario.Pipeline, scenario.Requests, options, "%2Fper%2Fdir");
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

        using var scenario = PipelineScenario.Decorating(location: ambient);
        var options = perCall is null ? null : new OpenCodeRequestOptions { Location = perCall };

        _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options, CancellationToken.None);

        var request = scenario.Requests.Single();
        var wireDirectory = request.Headers.TryGetValue("x-opencode-directory", out var directoryHeader)
            ? Uri.UnescapeDataString(directoryHeader)
            : null;
        await Assert.That(wireDirectory).IsEqualTo(expected?.Directory);
        await Assert.That(request.Headers).DoesNotContainKey("x-opencode-workspace");
    }
}
