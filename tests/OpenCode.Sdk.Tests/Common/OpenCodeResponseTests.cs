using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Common;

public sealed class OpenCodeResponseTests
{
    [Test]
    public async Task Constructor_Should_Default_To_The_Success_Path()
    {
        var response = new EmptyResponse
        {
            Status = 200,
        };

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Error).IsNull();
        await Assert.That(response.RawBody).IsNull();
    }

    [Test]
    public async Task SessionListResponse_Should_Retain_The_Caller_Owned_Page_Reference()
    {
        var session = new GeneratedJsonSerializer()
            .Deserialize<SessionInfo>(new FixtureLoader().LoadJson("Serialization.known-session.json"));
        var sessions = new List<SessionInfo> { session, };
        var response = new SessionListResponse
        {
            Status = 200,
            Sessions = sessions,
            Cursor = new ListCursor(),
        };

        sessions.Add(null!);

        await Assert.That(response.Sessions).Count().IsEqualTo(2);
        await Assert.That(response.Sessions[1]).IsNull();
    }

    /// <summary>
    /// An error body can echo whatever the request carried, so a logged response must not print
    /// it: the printed shape keeps the status and the typed error, and the raw body stays on its
    /// property for a caller who asks for it.
    /// </summary>
    [Test]
    public async Task ToString_Should_Not_Print_The_Raw_Error_Body()
    {
        const string rawBody = "{\"_tag\":\"InvalidRequestError\",\"message\":\"rejected sk-live-secret-token\"}";
        var response = new EmptyResponse
        {
            Status = 400,
            IsError = true,
            Error = new InvalidRequestError { Message = "rejected" },
            RawBody = rawBody,
        };

        var printed = response.ToString();

        await Assert.That(printed).DoesNotContain("sk-live-secret-token");
        await Assert.That(printed).DoesNotContain(nameof(OpenCodeResponse.RawBody));
        await Assert.That(printed).Contains("Status = 400");
        await Assert.That(printed).Contains("IsError = True");
        await Assert.That(printed).Contains("Error = InvalidRequestError");
        await Assert.That(response.RawBody).IsEqualTo(rawBody);
    }

    /// <summary>The generated envelopes chain to the base printer, so none of them prints it either.</summary>
    [Test]
    public async Task Generated_Envelope_ToString_Should_Not_Print_The_Raw_Error_Body()
    {
        using var scenario = ContractScenario.Responding(
            System.Net.HttpStatusCode.NotFound,
            "{\"_tag\":\"FileNotFoundError\",\"path\":\"notes.txt\",\"message\":\"File not found\",\"echo\":\"sk-live-secret-token\"}");

        var response = await scenario.Client.FileSystem.ReadFileAsync(
            new FsReadRequest { Path = "notes.txt" }, OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.RawBody).Contains("sk-live-secret-token");
        await Assert.That(response.ToString()).DoesNotContain("sk-live-secret-token");
        await Assert.That(response.ToString()).Contains("Status = 404");
    }

    private sealed record EmptyResponse : OpenCodeResponse;
}
