using System.Net;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.Tests.Transport.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Transport;

/// <summary>
/// Caller request headers through the pipeline: the refusals at construction and call start, the
/// per-call copy an event stream keeps across its reconnect, and the terminal upgrades they never
/// reach. The decoration order itself is the decoration policy's tests.
/// </summary>
public sealed class PipelineRequestHeadersTests
{
    private const string OperationId = "test.operation";
    private const string CallerValue = "caller";
    private const string AuthorizationValue = "Bearer caller";
    private const string InvalidNameReason = " contains an invalid HTTP token name.";
    private const string InvalidValueReason = " value for 'X-Probe' has a refused character at position 3.";
    private const string Latin1ValueReason = " value for 'X-Probe' has a refused character at position 2.";
    private const string NullValueReason = " value for 'X-Probe' is null.";
    private const string ValidValue = "valid";
    private const string CarriageValue = "bad\rvalue";
    private const string LineFeedValue = "bad\nvalue";
    private const string NulValue = "bad\0value\0";
    private const string Latin1Value = "ok\u0080value\u0081";
    private const string EuroValue = "bad€value€";
    private const string ClientValue = "client";
    private const string BeforeValue = "before";

    private static readonly Uri Endpoint = new("http://localhost:4096");

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    public async Task Headers_Should_Withhold_The_Missing_Credential_Hint_When_Authorization_Was_Sent(bool clientWide, bool stream)
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var options = new OpenCodeClientOptions { Endpoint = Endpoint };
        if (clientWide)
        {
            options.Headers.Add("Authorization", AuthorizationValue);
        }

        using var pipeline = Pipeline.Create(http, options);
        var call = clientWide ? null : new OpenCodeRequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = AuthorizationValue },
        };
        var failure = await Assert.That(async () =>
        {
            if (stream)
            {
                await using var events = pipeline.ExecuteStreamAsync(OperationId, HttpMethod.Get, "/api/event",
                    new TestStreamAdapter(), call, CancellationToken.None).GetAsyncEnumerator();
                _ = await events.MoveNextAsync();
            }
            else
            {
                _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", ServerInfoResponseAdapter.Instance, call, CancellationToken.None);
            }
        }).Throws<OpenCodeApiException>();

        await Assert.That(failure!.Message).IsEqualTo("The opencode API returned status 401.");
        await Assert.That(handler.Requests.Single().Authorization).IsEqualTo(AuthorizationValue);
    }

    [Test]
    [Arguments(true, "bad name", ValidValue, InvalidNameReason)]
    [Arguments(false, "bad:name", ValidValue, InvalidNameReason)]
    [Arguments(true, "X-Probe", CarriageValue, InvalidValueReason)]
    [Arguments(false, "X-Probe", LineFeedValue, InvalidValueReason)]
    [Arguments(false, "X-Probe", CarriageValue, InvalidValueReason)]
    [Arguments(true, "X-Probe", LineFeedValue, InvalidValueReason)]
    [Arguments(true, "", ValidValue, InvalidNameReason)]
    [Arguments(false, "é", ValidValue, InvalidNameReason)]
    [Arguments(true, "X-Probe", null, NullValueReason)]
    [Arguments(false, "X-Probe", null, NullValueReason)]
    [Arguments(true, "X-Probe", NulValue, InvalidValueReason)]
    [Arguments(false, "X-Probe", NulValue, InvalidValueReason)]
    [Arguments(true, "X-Probe", Latin1Value, Latin1ValueReason)]
    [Arguments(false, "X-Probe", Latin1Value, Latin1ValueReason)]
    [Arguments(true, "X-Probe", EuroValue, InvalidValueReason)]
    [Arguments(false, "X-Probe", EuroValue, InvalidValueReason)]
    public async Task Headers_Should_Validate_Before_Sending(bool clientWide, string name, string? value, string reason)
    {
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);
        var options = new OpenCodeClientOptions { Endpoint = Endpoint };
        if (clientWide)
        {
            // Callers can supply null despite the non-nullable dictionary contract.
            options.Headers.Add(name, value!);
        }

        var refusal = await Assert.That(async () =>
        {
            using var pipeline = Pipeline.Create(http, options);
            var call = new OpenCodeRequestOptions
            {
                // Deliberately exercise a null value supplied against the annotated contract.
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value! },
            };
            _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(),
                clientWide ? null : call, CancellationToken.None);
        }).Throws<ArgumentException>();

        await RouteRefusal.AssertAsync(refusal, (clientWide ? "OpenCodeClientOptions.Headers" : "OpenCodeRequestOptions.Headers") + reason,
            clientWide ? "options" : "requestOptions");
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Headers_Should_Refuse_A_Null_PerCall_Name()
    {
        // A caller-owned dictionary can enumerate a null key despite its annotations.
        IEnumerable<KeyValuePair<string, string>> entries = [new(null!, ValidValue)];
        var headers = Substitute.For<IReadOnlyDictionary<string, string>>();
        headers.GetEnumerator().Returns(_ => entries.GetEnumerator());
        using var scenario = PipelineScenario.Responding();

        var refusal = await Assert.That(async () => _ = await scenario.Pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info",
            new RecordingResponseAdapter(), new OpenCodeRequestOptions { Headers = headers }, CancellationToken.None)).Throws<ArgumentException>();

        await RouteRefusal.AssertAsync(refusal, "OpenCodeRequestOptions.Headers" + InvalidNameReason, "requestOptions");
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    public async Task Headers_Should_Validate_At_Client_Construction()
    {
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);
        var options = new OpenCodeClientOptions { Endpoint = Endpoint };
        options.Headers.Add("bad name", ValidValue);

        var refusal = Assert.Throws<ArgumentException>(() => _ = Pipeline.Create(http, options));

        await RouteRefusal.AssertAsync(refusal, "OpenCodeClientOptions.Headers" + InvalidNameReason, "options");
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    [Arguments("aUtHoRiZaTiOn", "Password")]
    [Arguments("X-OPENCODE-DIRECTORY", "Location")]
    public async Task Headers_Should_Refuse_Only_Conflicting_Client_Members(string name, string member)
    {
        var options = new OpenCodeClientOptions
        {
            Endpoint = Endpoint,
            Password = member is "Password" ? "" : null,
            Location = member is "Location" ? new LocationSelector() : null,
        };
        options.Headers.Add(name, CallerValue);
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);

        var refusal = Assert.Throws<ArgumentException>(() => _ = Pipeline.Create(http, options));

        var text = $"OpenCodeClientOptions.Headers conflicts with OpenCodeClientOptions.{member}.";
        await Assert.That(refusal.Message).IsEqualTo(
#if NET
            text + " (Parameter 'options')");
#else
            text + Environment.NewLine + "Parameter name: options");
#endif
        await Assert.That(refusal.ParamName).IsEqualTo("options");
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Headers_Should_Not_Reach_Terminal_Upgrades(bool persistent)
    {
        await using var wire = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.NotFound });
        var options = new OpenCodeClientOptions { Endpoint = wire.Endpoint, Password = "secret" };
        options.Headers.Add("X-Http-Only", CallerValue);
        using var client = new OpenCodeClient(options);

        _ = await Assert.That(async () =>
        {
            if (persistent)
            {
                await using var session = await client.PersistentPtys.GetPersistentPtyClient("pty_headers").ConnectAsync();
            }
            else
            {
                await using var session = await client.Ptys.GetPtyClient("pty_headers").ConnectAsync();
            }
        }).Throws<OpenCodeTransportException>();

        var upgrade = wire.Requests.Single();
        await Assert.That(upgrade.Headers["Upgrade"]).IsEqualTo("websocket");
        await Assert.That(upgrade.Headers["Authorization"]).IsEqualTo("Basic b3BlbmNvZGU6c2VjcmV0");
        await Assert.That(upgrade.Headers).DoesNotContainKey("X-Http-Only");
    }

    [Test]
    public async Task Headers_Should_Be_Validated_Before_Stream_Enumeration()
    {
        using var scenario = PipelineScenario.Responding();
        var options = new OpenCodeRequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Probe"] = CarriageValue },
        };
        var refusal = await Assert.That(() => scenario.Pipeline.ExecuteStreamAsync(OperationId, HttpMethod.Get,
            "/api/event", new TestStreamAdapter(), options, CancellationToken.None)).Throws<ArgumentException>();

        await RouteRefusal.AssertAsync(refusal, "OpenCodeRequestOptions.Headers" + InvalidValueReason, "requestOptions");
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Headers_Should_Snapshot_At_Stream_Start_For_The_Open_And_Reconnect(bool initiallyEmpty)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!initiallyEmpty)
        {
            headers.Add("X-Probe", BeforeValue);
        }

        var time = new ManualTimeProvider();
        using var handler = new RecordingHttpHandler(static _ => ScriptedEventServer.StreamOf(
            WireBodyData.Frames(ScriptedEventServer.Connected), ends: true));
        using var http = new HttpClient(handler);
        var clientOptions = new OpenCodeClientOptions { Endpoint = PipelineFactory.Endpoint };
        clientOptions.Headers.Add("X-Client", ClientValue);
        using var pipeline = new Pipeline(http, ownsHttpClient: false, clientOptions, time: time);
        var stream = pipeline.ExecuteStreamAsync("event.subscribe", HttpMethod.Get, "/api/event", EventSubscribeResponseStreamAdapter.Instance,
            new OpenCodeRequestOptions { Headers = headers }, CancellationToken.None);
        headers["X-Probe"] = "after-start";
        await using var events = stream.GetAsyncEnumerator();
        await Assert.That(await events.MoveNextAsync()).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        headers["X-Probe"] = "after-open";
        var next = events.MoveNextAsync().AsTask();
        await time.WaitForArmedAsync(TimeSpan.FromSeconds(1), HangGuard.Bound);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();

        foreach (var request in handler.Requests)
        {
            await Assert.That(request.Headers).ContainsKey("X-Client");
        }

        await Assert.That(handler.Requests.Select(static request => request.Headers["X-Client"]))
            .IsEquivalentTo([ClientValue, ClientValue], CollectionOrdering.Matching);
        string[] expected = initiallyEmpty ? ["absent", "absent"] : [BeforeValue, BeforeValue];
        await Assert.That(handler.Requests.Select(static request => request.Headers.TryGetValue("X-Probe", out var value) ? value : "absent"))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
