using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Handoff;

public sealed class ServiceHandoffTicketAdapterTests
{
    private static readonly FixtureLoader Fixtures = new();

    [Test]
    [Arguments("BackgroundService.pty-handoff-ticket-trailing-garbage.json")]
    [Arguments("BackgroundService.pty-handoff-ticket-second-value.json")]
    public async Task AdaptSuccess_Should_Refuse_Content_After_A_Valid_Ticket_Document(string fixture)
    {
        var bytes = Encoding.UTF8.GetBytes(Fixtures.LoadJson(fixture));

        var exception = await Assert.That(() => ServiceHandoffTicketAdapter.Instance.AdaptSuccess(200, bytes))
            .Throws<OpenCodeTransportException>();

        await AssertMalformedBodyAsync(exception!);
    }

    [Test]
    [Arguments("BackgroundService.pty-handoff-ticket-trailing-garbage.json")]
    [Arguments("BackgroundService.pty-handoff-ticket-second-value.json")]
    public async Task Adapt_Should_Refuse_Content_After_A_Valid_Ticket_Document(string fixture)
    {
        var body = Fixtures.LoadJson(fixture);

        var exception = await Assert.That(() => ServiceHandoffTicketAdapter.Instance.Adapt(200, body))
            .Throws<OpenCodeTransportException>();

        await AssertMalformedBodyAsync(exception!);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Adapt_Should_Admit_Trailing_Whitespace_And_Preserve_The_Raw_Ticket(bool utf8)
    {
        var body = Fixtures.LoadJson("BackgroundService.pty-handoff-unknown-members.json") + " \t\r\n";
        using var expected = JsonDocument.Parse(body);

        var response = utf8
            ? ServiceHandoffTicketAdapter.Instance.AdaptSuccess(200, Encoding.UTF8.GetBytes(body))
            : ServiceHandoffTicketAdapter.Instance.Adapt(200, body);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.HasHandoffMember).IsTrue();
        await Assert.That(response.Handoff).IsNotNull();
        await Assert.That(response.Handoff!.Value.GetRawText()).IsEqualTo(expected.RootElement.GetProperty("handoff").GetRawText());
    }

    private static async Task AssertMalformedBodyAsync(OpenCodeTransportException exception)
    {
        await Assert.That(exception.InnerException).IsAssignableTo<JsonException>();
        await Assert.That(exception.Message).Contains("malformed success body");
    }
}
