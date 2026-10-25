using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class FrameDispatchTests
{
    private const string FirstPayload = "{\"value\":\"first\"}";
    private const string DieCause = "[{\"tag\":\"Die\",\"detail\":\"boom\"}]";
    private const string MalformedData = "not json";

    [Test]
    public async Task ReadPayload_Should_Yield_The_Default_Frame_Payload()
    {
        var frame = new ServerSentEvent(ServerSentEvent.DefaultName, FirstPayload);

        var payload = FrameDispatch.ReadPayload(frame, new TestStreamAdapter());

        await Assert.That(payload.Value).IsEqualTo("first");
    }

    [Test]
    public async Task ReadPayload_Should_Throw_The_Typed_Failure_For_The_Declared_Failure_Frame()
    {
        var frame = new ServerSentEvent(TestStreamAdapter.StreamFailureEventName, DieCause);

        var exception = Assert.Throws<OpenCodeStreamFailureException>(
            () => _ = FrameDispatch.ReadPayload(frame, new TestStreamAdapter()));

        var cause = (TestStreamFailureCause)exception.Causes.Single();
        await Assert.That(cause.Tag).IsEqualTo("Die");
        await Assert.That(cause.Detail).IsEqualTo("boom");
    }

    [Test]
    public async Task ReadPayload_Should_Refuse_A_Frame_Named_Outside_The_Contract()
    {
        var frame = new ServerSentEvent("surprise", FirstPayload);

        var exception = Assert.Throws<OpenCodeTransportException>(
            () => _ = FrameDispatch.ReadPayload(frame, new TestStreamAdapter()));

        await Assert.That(exception).IsNotTypeOf<OpenCodeStreamFailureException>();
        await Assert.That(exception.Message).Contains("surprise");
    }

    [Test]
    public async Task ReadPayload_Should_Refuse_A_Malformed_Payload()
    {
        var frame = new ServerSentEvent(ServerSentEvent.DefaultName, MalformedData);

        var exception = Assert.Throws<OpenCodeTransportException>(
            () => _ = FrameDispatch.ReadPayload(frame, new TestStreamAdapter()));

        await Assert.That(exception.InnerException).IsTypeOf<JsonException>();
    }

    [Test]
    public async Task ReadPayload_Should_Refuse_A_Null_Payload()
    {
        var frame = new ServerSentEvent(ServerSentEvent.DefaultName, "null");

        var exception = Assert.Throws<OpenCodeTransportException>(
            () => _ = FrameDispatch.ReadPayload(frame, new TestStreamAdapter()));

        await Assert.That(exception.Message).Contains("null");
    }

    [Test]
    public async Task ReadPayload_Should_Refuse_A_Malformed_Failure_Cause()
    {
        var frame = new ServerSentEvent(TestStreamAdapter.StreamFailureEventName, MalformedData);

        var exception = Assert.Throws<OpenCodeTransportException>(
            () => _ = FrameDispatch.ReadPayload(frame, new TestStreamAdapter()));

        await Assert.That(exception).IsNotTypeOf<OpenCodeStreamFailureException>();
        await Assert.That(exception.InnerException).IsTypeOf<JsonException>();
    }

    [Test]
    public async Task ReadPayload_Should_Refuse_A_Null_Failure_Cause()
    {
        var frame = new ServerSentEvent(TestStreamAdapter.StreamFailureEventName, "null");

        var exception = Assert.Throws<OpenCodeTransportException>(
            () => _ = FrameDispatch.ReadPayload(frame, new TestStreamAdapter()));

        await Assert.That(exception).IsNotTypeOf<OpenCodeStreamFailureException>();
        await Assert.That(exception.Message).Contains("null failure cause");
    }

    [Test]
    [MethodDataSource(nameof(EventRefusals))]
    [MethodDataSource(nameof(LogRefusals))]
    public async Task ReadPayload_Should_Keep_The_Failure_When_The_Unknown_Arm_Refuses(
        string name, string body, Func<string, object> read)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(read);
        var exception = Assert.Throws<OpenCodeTransportException>(() => _ = read(body));

        await Assert.That(exception.Message).IsEqualTo("The opencode event stream produced a malformed frame payload.");
        await Assert.That(exception.InnerException).IsTypeOf<JsonException>();
    }

    public static IEnumerable<object[]> EventRefusals() => Refusals(static body => FrameDispatch.ReadPayload(
        new ServerSentEvent(ServerSentEvent.DefaultName, body), EventSubscribeResponseStreamAdapter.Instance));

    public static IEnumerable<object[]> LogRefusals() => Refusals(static body => FrameDispatch.ReadPayload(
        new ServerSentEvent(ServerSentEvent.DefaultName, body), SessionLogResponseStreamAdapter.Instance));

    private static IEnumerable<object[]> Refusals(Func<string, object> read) =>
    [
        ["invalid JSON", MalformedData, read],
        ["array", new JsonArray().ToJsonString(), read],
        ["missing type", new JsonObject().ToJsonString(), read],
        ["numeric type", new JsonObject { ["type"] = 42 }.ToJsonString(), read],
        ["empty type", new JsonObject { ["type"] = "" }.ToJsonString(), read],
        ["whitespace type", new JsonObject { ["type"] = "  " }.ToJsonString(), read],
    ];

    [Test]
    public async Task ReadPayload_Should_Keep_The_Primary_Failure_For_An_Unreadable_Rpc_Event()
    {
        var frame = new ServerSentEvent(ServerSentEvent.DefaultName, WireBodyData.EventWithoutData("rpc.foo"));
        var exception = Assert.Throws<OpenCodeTransportException>(
            () => _ = FrameDispatch.ReadPayload(frame, EventSubscribeResponseStreamAdapter.Instance));

        await Assert.That(exception.Message).IsEqualTo("The opencode event stream produced a malformed frame payload.");
        await Assert.That(exception.InnerException).IsTypeOf<JsonException>();
        await Assert.That(exception.InnerException!.Message)
            .IsEqualTo("JSON deserialization for type 'OpenCode.Sdk.Models.EventRpc' was missing required properties including: 'data'.");
    }
}
