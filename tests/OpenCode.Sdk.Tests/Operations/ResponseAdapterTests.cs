using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Operations;

public sealed class ResponseAdapterTests
{
    private static readonly string[] UnauthorizedTags = ["UnauthorizedError"];

    [Test]
    public async Task ReadBarePayload_Should_Deserialize_The_Body()
    {
        var payload = ProbeAdapter.Bare(WireBodyData.InfoOk, OpenCodeJsonContext.Default.ServerInfo);

        await Assert.That(payload.Version).IsEqualTo("0.0.0-test");
        await Assert.That(payload.Pid).IsEqualTo(42);
    }

    [Test]
    public async Task ReadBarePayload_Should_Treat_Malformed_Bodies_As_Protocol_Failures()
    {
        _ = Assert.Throws<OpenCodeTransportException>(() =>
            _ = ProbeAdapter.Bare("not json", OpenCodeJsonContext.Default.ServerInfo));
    }

    [Test]
    public async Task ReadBarePayload_Should_Treat_Null_Bodies_As_Protocol_Failures()
    {
        _ = Assert.Throws<OpenCodeTransportException>(() =>
            _ = ProbeAdapter.Bare("null", OpenCodeJsonContext.Default.ServerInfo));
    }

    [Test]
    public async Task ReadBarePayload_Should_Treat_Null_Utf8_Bodies_As_Protocol_Failures()
    {
        _ = Assert.Throws<OpenCodeTransportException>(() =>
            _ = ProbeAdapter.Bare("null"u8, OpenCodeJsonContext.Default.ServerInfo));
    }

    [Test]
    public async Task ReadTolerantError_Should_Keep_A_Known_Tag_At_Its_Declared_Status()
    {
        var error = ProbeAdapter.Error("{\"_tag\":\"UnauthorizedError\",\"message\":\"no\"}", UnauthorizedTags);

        await Assert.That(error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task ReadTolerantError_Should_Keep_The_Unknown_Carrier_For_An_Unknown_Tag()
    {
        var error = ProbeAdapter.Error("{\"_tag\":\"BrandNewError\",\"detail\":1}", UnauthorizedTags);

        await Assert.That(error).IsTypeOf<UnknownOpenCodeError>();
        await Assert.That(((UnknownOpenCodeError)error!).Tag).IsEqualTo("BrandNewError");
    }

    [Test]
    public async Task ReadTolerantError_Should_Downgrade_A_Known_Tag_At_The_Wrong_Status()
    {
        var error = ProbeAdapter.Error("{\"_tag\":\"SessionNotFoundError\",\"sessionID\":\"s\",\"message\":\"m\"}", UnauthorizedTags);

        await Assert.That(error).IsTypeOf<UnknownOpenCodeError>();
        await Assert.That(((UnknownOpenCodeError)error!).Tag).IsEqualTo("SessionNotFoundError");
        await Assert.That(((UnknownOpenCodeError)error).Payload.GetProperty("sessionID").GetString()).IsEqualTo("s");
    }

    [Test]
    public async Task ReadTolerantError_Should_Downgrade_A_Known_Tag_At_An_Undeclared_Status()
    {
        var error = ProbeAdapter.Error("{\"_tag\":\"UnauthorizedError\",\"message\":\"no\"}", allowedTags: null);

        await Assert.That(error).IsTypeOf<UnknownOpenCodeError>();
    }

    [Test]
    public async Task ReadTolerantError_Should_Return_Null_For_A_Malformed_Body()
    {
        var error = ProbeAdapter.Error("<html>oops</html>", UnauthorizedTags);

        await Assert.That(error).IsNull();
    }

    private sealed class ProbeAdapter : ResponseAdapter<TestResponse>
    {
        public override StatusVerdict Classify(int status) => status switch
        {
            200 => StatusVerdict.Success,
            >= 200 and < 300 => StatusVerdict.UndeclaredSuccess,
            _ => StatusVerdict.UndeclaredError,
        };

        public static TPayload Bare<TPayload>(string rawBody, JsonTypeInfo<TPayload> typeInfo) =>
            ReadBarePayload(rawBody, typeInfo);

        public static TPayload Bare<TPayload>(ReadOnlySpan<byte> utf8Body, JsonTypeInfo<TPayload> typeInfo) =>
            ReadBarePayload(utf8Body, typeInfo);

        public static IOpenCodeError? Error(string rawBody, string[]? allowedTags) =>
            ReadTolerantError(rawBody, allowedTags);

        public override TestResponse AdaptSuccess(int status, ReadOnlySpan<byte> utf8Body) =>
            new()
            {
                Status = status,
            };

        public override TestResponse Adapt(int status, string rawBody) =>
            new()
            {
                Status = status,
            };
    }
}
