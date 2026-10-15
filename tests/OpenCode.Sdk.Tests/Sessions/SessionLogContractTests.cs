using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// The durable log's representative runtime corpus exercises two of 40 durable branches,
/// the log-synced watermark, and unknown-carrier behavior. Generator tests mechanically
/// prove complete converter mappings and source-generated registry coverage.
/// </summary>
public sealed class SessionLogContractTests
{
    [Test]
    public async Task GetLogAsync_Should_Type_Each_Frame_By_Its_Own_Tag()
    {
        using var scenario = ContractScenario.RespondingWithFrames(WireBodyData.Frames(
            WireBodyData.SessionCreatedEvent,
            WireBodyData.SessionDeletedEvent,
            WireBodyData.LogSyncedEvent));

        var items = await CollectAsync(scenario);

        await Assert.That(items).Count().IsEqualTo(3);
        await Assert.That(items[0]).IsTypeOf<SessionCreated>();
        await Assert.That(items[1]).IsTypeOf<SessionDeleted>();
        await Assert.That(items[2]).IsTypeOf<EventLogSynced>();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
        await Assert
            .That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/session/ses_9/log"));
    }

    [Test]
    public async Task GetLogAsync_Should_Carry_Both_Durable_Envelope_Versions()
    {
        using var scenario = ContractScenario.RespondingWithFrames(WireBodyData.Frames(
            WireBodyData.SessionCreatedEvent,
            WireBodyData.SessionDeletedEvent));

        var items = await CollectAsync(scenario);

        var created = (SessionCreated)items[0];
        var deleted = (SessionDeleted)items[1];
        await Assert.That(created.Durable.Version).IsEqualTo(1d);
        await Assert.That(created.Durable.Seq).IsEqualTo(1L);
        await Assert.That(created.Data.SessionId).IsEqualTo("ses_9");
        await Assert.That(deleted.Durable.Version).IsEqualTo(2d);
        await Assert.That(deleted.Durable.AggregateId).IsEqualTo("ses_9");
    }

    [Test]
    public async Task GetLogAsync_Should_Read_The_Durable_Envelope_Through_The_Union_Interface()
    {
        using var scenario = ContractScenario.RespondingWithFrames(WireBodyData.Frames(
            WireBodyData.SessionCreatedEvent,
            WireBodyData.SessionDeletedEvent,
            WireBodyData.LogSyncedEvent));

        var items = await CollectAsync(scenario);

        // A generic relay reads the shared envelope without knowing any of the 43 leaf types.
        var durable = items.OfType<ISessionEventDurable>().ToArray();
        await Assert.That(durable).Count().IsEqualTo(2);
        await Assert.That(durable[0].Id).IsEqualTo("evt_1");
        await Assert.That(durable[0].Durable!.Seq).IsEqualTo(1L);
        await Assert.That(durable[0].Durable!.Version).IsEqualTo(1d);
        await Assert.That(durable[0].Durable!.AggregateId).IsEqualTo("ses_9");
        await Assert.That(durable[1].Id).IsEqualTo("evt_2");
        await Assert.That(durable[1].Durable!.Seq).IsEqualTo(2L);
        await Assert.That(durable[1].Durable!.Version).IsEqualTo(2d);
        await Assert.That(durable[1].Durable!.AggregateId).IsEqualTo("ses_9");

        // The sync marker is not a durable event; it carries its own watermark instead.
        var marker = (EventLogSynced)items[2];
        await Assert.That(marker).IsNotAssignableTo<ISessionEventDurable>();
        await Assert.That(marker.Seq).IsEqualTo(2L);
    }

    [Test]
    public async Task GetLogAsync_Should_Group_Durable_Events_Apart_From_The_Watermark()
    {
        using var scenario = ContractScenario.RespondingWithFrames(WireBodyData.Frames(
            WireBodyData.SessionCreatedEvent,
            WireBodyData.LogSyncedEvent));

        var items = await CollectAsync(scenario);

        await Assert.That(items[0]).IsAssignableTo<ISessionEventDurable>();
        await Assert.That(items[1]).IsNotAssignableTo<ISessionEventDurable>();
        await Assert.That(items[1]).IsAssignableTo<ISessionLogItem>();
    }

    [Test]
    public async Task GetLogAsync_Should_Preserve_A_Tag_No_Variant_Owns()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.Frames(WireBodyData.UnknownLogEvent));

        var items = await CollectAsync(scenario);

        var carrier = (UnknownSessionLogItem)items.Single();
        await Assert.That(carrier.Type).IsEqualTo("session.invented.tomorrow");
        await Assert.That(carrier.Payload.GetProperty("id").GetString()).IsEqualTo("evt_3");
    }

    [Test]
    public async Task GetLogAsync_Should_Expose_A_Typed_Mid_Stream_Failure_Cause()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.Frames(WireBodyData.SessionCreatedEvent)
            + WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.StreamFailureCause));

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeStreamFailureException>();

        var cause = (StreamFailureCauseDie)exception!.Causes.Single();
        await Assert.That(cause.Defect.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(cause.Defect.GetString()).IsEqualTo("boom");
    }

    [Test]
    public async Task GetLogAsync_Should_Preserve_An_Unknown_Failure_Cause_Tag()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.UnknownStreamFailureCause));

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeStreamFailureException>();

        var cause = (UnknownStreamFailureCause)exception!.Causes.Single();
        await Assert.That(cause.Tag).IsEqualTo("FutureCause");
        await Assert.That(cause.Payload.GetProperty("detail").GetString()).IsEqualTo("later");
    }

    [Test]
    public async Task GetLogAsync_Should_Refuse_The_Known_Impossible_Failure_Cause_Tag()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.ImpossibleStreamFailureCause));

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception).IsNotTypeOf<OpenCodeStreamFailureException>();
        await Assert.That(exception!.InnerException).IsTypeOf<JsonException>();
        await Assert.That(exception.InnerException!.Message).Contains("admits no JSON value");
    }

    [Test]
    [Arguments(WireBodyData.StreamInterruptNullCause, null)]
    [Arguments(WireBodyData.StreamInterruptNumberCause, 42d)]
    public async Task GetLogAsync_Should_Expose_An_Interrupt_Failure_Cause(string body, double? expectedFiberId)
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", body));

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeStreamFailureException>();

        var cause = (StreamFailureCauseInterrupt)exception!.Causes.Single();
        await Assert.That(cause.FiberId).IsEqualTo(expectedFiberId);
    }

    [Test]
    public async Task GetLogAsync_Should_Preserve_Multiple_Failure_Causes()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.MultipleStreamFailureCauses));

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeStreamFailureException>();

        await Assert.That(exception!.Causes).Count().IsEqualTo(2);
        await Assert.That(exception.Causes[0]).IsTypeOf<StreamFailureCauseDie>();
        await Assert.That(exception.Causes[1]).IsTypeOf<StreamFailureCauseInterrupt>();
    }

    [Test]
    public async Task GetLogAsync_Should_Preserve_An_Empty_Failure_Cause()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.EmptyStreamFailureCause));

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeStreamFailureException>();

        await Assert.That(exception!.Causes).IsEmpty();
    }

    [Test]
    [Arguments(400, WireBodyData.InvalidRequestError, typeof(InvalidRequestError))]
    [Arguments(401, WireBodyData.UnauthorizedError, typeof(UnauthorizedError))]
    [Arguments(404, WireBodyData.SessionNotFoundError, typeof(SessionNotFoundError))]
    public async Task GetLogAsync_Should_Throw_Each_Declared_Error_Before_Opening_The_Stream(int status,
        string body,
        Type expectedErrorType)
    {
        using var scenario = ContractScenario.Responding((HttpStatusCode)status, body);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(scenario))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(status);
        await Assert.That(exception.Error!.GetType()).IsEqualTo(expectedErrorType);
    }

    [Test]
    public async Task GetLogAsync_Should_Compose_The_Resume_Cursor_And_Follow_Flag()
    {
        using var scenario = ContractScenario.RespondingWithFrames(
            WireBodyData.Frames(WireBodyData.LogSyncedEvent));

        _ = await CollectAsync(scenario, new SessionLogRequest
        {
            After = 12,
            Follow = QueryBoolean.True,
        });

        await Assert
            .That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(
                new Uri("http://localhost:4096/api/experimental/session/ses_9/log?after=12&follow=true"));
    }

    private static async Task<List<ISessionLogItem>> CollectAsync(ContractScenario scenario,
        SessionLogRequest? request = null)
    {
        var items = new List<ISessionLogItem>();
        await foreach (var item in scenario.Client.Sessions.GetSessionClient("ses_9").GetLogAsync(request))
        {
            items.Add(item);
        }

        return items;
    }
}
