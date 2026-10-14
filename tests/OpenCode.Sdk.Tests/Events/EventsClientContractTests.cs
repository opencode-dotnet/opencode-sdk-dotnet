using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The live bus keeps a representative runtime corpus: one shared durable/live leaf,
/// one unknown tag, and the declared failure channel. Generator tests own structural breadth.
/// </summary>
public sealed class EventsClientContractTests
{
    [Test]
    public async Task SubscribeAsync_Should_Type_A_Shared_Durable_Leaf()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.Frames(WireBodyData.SessionCreatedEvent));

        var items = await CollectAsync(scenario);

        var created = (SessionCreated)items.Single();
        await Assert.That(created).IsAssignableTo<IEvent>();
        await Assert.That(created).IsAssignableTo<ISessionEventDurable>();
        await Assert.That(created.Data.SessionId).IsEqualTo("ses_9");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/event"));
    }

    [Test]
    public async Task SubscribeAsync_Should_Preserve_An_Unknown_Event_Tag()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.Frames(WireBodyData.UnknownLogEvent));

        var items = await CollectAsync(scenario);

        var unknown = (UnknownEvent)items.Single();
        await Assert.That(unknown.Type).IsEqualTo("session.invented.tomorrow");
        await Assert.That(unknown.Payload.GetProperty("id").GetString()).IsEqualTo("evt_3");
    }

    [Test]
    public async Task SubscribeAsync_Should_Expose_A_Typed_Mid_Stream_Failure_Cause()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.StreamFailureCause));

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeStreamFailureException>();

        var cause = (StreamFailureCauseDie)exception!.Causes.Single();
        await Assert.That(cause.Defect.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(cause.Defect.GetString()).IsEqualTo("boom");
    }

    [Test]
    [Arguments(400, WireBodyData.InvalidRequestError, typeof(InvalidRequestError))]
    [Arguments(401, WireBodyData.UnauthorizedError, typeof(UnauthorizedError))]
    public async Task SubscribeAsync_Should_Throw_Each_Declared_Error_Before_Opening_The_Stream(int status,
        string body, Type expectedErrorType)
    {
        using var scenario = ContractScenario.Responding((HttpStatusCode)status, body);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(status);
        await Assert.That(exception.Error!.GetType()).IsEqualTo(expectedErrorType);
    }

    private static async Task<List<IEvent>> CollectAsync(ContractScenario scenario)
    {
        var items = new List<IEvent>();
        await foreach (var item in scenario.Client.Events.SubscribeAsync())
        {
            items.Add(item);
        }

        return items;
    }
}
