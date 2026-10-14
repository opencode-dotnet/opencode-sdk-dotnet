using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

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

        await Assert.That(exception.InnerException).IsTypeOf<System.Text.Json.JsonException>();
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
        await Assert.That(exception.InnerException).IsTypeOf<System.Text.Json.JsonException>();
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
}
