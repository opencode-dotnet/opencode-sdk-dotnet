using System.Net;
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

    private static readonly Uri Endpoint = new("http://localhost:4096");

    [Test]
    [Arguments(true, "bad name", "valid", "Headers contains an invalid HTTP token name.")]
    [Arguments(false, "bad:name", "valid", "Headers contains an invalid HTTP token name.")]
    [Arguments(true, "X-Probe", "bad\rvalue", "Headers contains a value with CR or LF.")]
    [Arguments(false, "X-Probe", "bad\nvalue", "Headers contains a value with CR or LF.")]
    [Arguments(false, "X-Probe", "bad\rvalue", "Headers contains a value with CR or LF.")]
    [Arguments(true, "X-Probe", "bad\nvalue", "Headers contains a value with CR or LF.")]
    [Arguments(true, "", "valid", "Headers contains an invalid HTTP token name.")]
    [Arguments(false, "é", "valid", "Headers contains an invalid HTTP token name.")]
    public async Task Headers_Should_Validate_Before_Sending(bool clientWide, string name, string value, string reason)
    {
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);
        var options = new OpenCodeClientOptions { Endpoint = Endpoint };
        if (clientWide)
        {
            options.Headers.Add(name, value);
        }

        var refusal = await Assert.That(async () =>
        {
            using var pipeline = Pipeline.Create(http, options);
            var call = new OpenCodeRequestOptions
            {
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value },
            };
            _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(),
                clientWide ? null : call, CancellationToken.None);
        }).Throws<ArgumentException>();

        await RouteRefusal.AssertAsync(refusal, reason, "headers");
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
        options.Headers.Add(name, "caller");
        using var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler);

        var refusal = Assert.Throws<ArgumentException>(() => _ = Pipeline.Create(http, options));

        var text = $"OpenCodeClientOptions.Headers contains {name}, which conflicts with OpenCodeClientOptions.{member}.";
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
        options.Headers.Add("X-Http-Only", "caller");
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
            Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Probe"] = "bad\rvalue" },
        };
        var refusal = await Assert.That(() => scenario.Pipeline.ExecuteStreamAsync(OperationId, HttpMethod.Get,
            "/api/event", new TestStreamAdapter(), options, CancellationToken.None)).Throws<ArgumentException>();

        await RouteRefusal.AssertAsync(refusal, "Headers contains a value with CR or LF.", "headers");
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
            headers.Add("X-Probe", "before");
        }

        var time = new ManualTimeProvider();
        using var handler = new RecordingHttpHandler(static _ => ScriptedEventServer.StreamOf(
            WireBodyData.Frames(ScriptedEventServer.Connected), ends: true));
        using var http = new HttpClient(handler);
        var clientOptions = new OpenCodeClientOptions { Endpoint = PipelineFactory.Endpoint };
        clientOptions.Headers.Add("X-Client", "client");
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
            .IsEquivalentTo(["client", "client"], CollectionOrdering.Matching);
        string[] expected = initiallyEmpty ? ["absent", "absent"] : ["before", "before"];
        await Assert.That(handler.Requests.Select(static request => request.Headers.TryGetValue("X-Probe", out var value) ? value : "absent"))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
