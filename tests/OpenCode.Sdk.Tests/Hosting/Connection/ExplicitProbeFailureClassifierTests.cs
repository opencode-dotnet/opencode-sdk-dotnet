using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class ExplicitProbeFailureClassifierTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4096");

    [Test]
    [Arguments(false, "Server at http://127.0.0.1:4096/ requires a password; set OpenCodeServerResolveOptions.Password")]
    [Arguments(true, "Server at http://127.0.0.1:4096/ rejected the password")]
    public async Task Map_Should_Use_The_Password_Dependent_Message_For_A_Tagged_Refusal(bool hasPassword, string expected)
    {
        var cause = new OpenCodeApiException("refused", 400, new UnauthorizedError { Message = "password required" }, rawBody: null);

        var mapped = ExplicitProbeFailureClassifier.Map(cause, Endpoint, hasPassword, default, default);

        await Assert.That(mapped).IsTypeOf<OpenCodeServerException>();
        await Assert.That(mapped.Message).IsEqualTo(expected);
        await Assert.That(mapped.InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    [Arguments(401)]
    [Arguments(500)]
    public async Task Map_Should_Report_An_Incompatible_Health_Response_For_An_Untagged_Status(int status)
    {
        var cause = new OpenCodeApiException("unexpected response", status, error: null, rawBody: null);

        var mapped = ExplicitProbeFailureClassifier.Map(cause, Endpoint, hasPassword: false, default, default);

        await Assert.That(mapped.Message).IsEqualTo(
            "Server at http://127.0.0.1:4096/ did not provide a compatible V2 health response: unexpected response");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    public async Task Map_Should_Report_An_Unreachable_Server_For_A_Transport_Fault()
    {
        var cause = new OpenCodeTransportException("send failed", new HttpRequestException("connection refused"));

        var mapped = ExplicitProbeFailureClassifier.Map(cause, Endpoint, hasPassword: false, default, default);

        await Assert.That(mapped.Message).IsEqualTo("Could not reach server at http://127.0.0.1:4096/: connection refused");
        await Assert.That(mapped.InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Map_Should_Report_An_Incompatible_Health_Response_For_A_Malformed_Body(bool nullBody)
    {
        var cause = nullBody
            ? new OpenCodeTransportException("The opencode API returned a null success body.")
            : new OpenCodeTransportException("malformed body", new JsonException("bad JSON"));

        var mapped = ExplicitProbeFailureClassifier.Map(cause, Endpoint, hasPassword: false, default, default);

        await Assert.That(mapped.Message).IsEqualTo("Server at http://127.0.0.1:4096/ did not provide a compatible V2 health response: " + cause.Message);
        await Assert.That(mapped.InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    public async Task Map_Should_Prefer_The_Callers_Cancellation_When_The_Bound_Also_Cancelled()
    {
        var caller = new CancellationToken(canceled: true);
        var bound = new CancellationToken(canceled: true);
        var cause = new OperationCanceledException("deadline", bound);

        var mapped = ExplicitProbeFailureClassifier.Map(cause, Endpoint, hasPassword: false, caller, bound);

        await Assert.That(mapped).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)mapped).CancellationToken).IsEqualTo(caller);
    }
}
