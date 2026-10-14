using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class SessionScopedEventBinderTests
{
    [Test]
    public async Task Bind_Should_Project_A_Required_Identifier_From_An_Event_Payload()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());

        var projection = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Qualified").SessionScopedEvent;
        await Assert.That(projection).IsNotNull();
        await Assert.That(projection!.InterfaceName).IsEqualTo("ISessionScopedEvent");
        await Assert.That(projection.PayloadPropertyName).IsEqualTo("Data");
        await Assert.That(projection.IdentifierPropertyName).IsEqualTo("SessionId");
    }

    [Test]
    public async Task Bind_Should_Qualify_A_Stabilized_Duplicate_Identifier()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());

        await Assert.That(plan.ImplicitAliases.Aliases["Session.ID_1"]).IsEqualTo("Session.ID");
        await Assert.That(plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Duplicate").SessionScopedEvent).IsNotNull();
    }

    [Test]
    public async Task Bind_Should_Normalize_A_Curated_Duplicate_Identifier_Through_The_Same_Alias_Map()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var row = SessionScopedEventScenario.Rule() with { Identifier = "Session.ID_1" };
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration(row));

        await Assert.That(plan.Models.OfType<ObjectModelPlan>().Where(static model => model.SessionScopedEvent is not null)
            .Select(static model => model.Name)).IsEquivalentTo(["Qualified", "Duplicate", "LogOnly"]);
    }

    [Test]
    public async Task Bind_Should_Project_An_Event_Only_The_Log_Union_Declares()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());

        var logOnly = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "LogOnly");
        await Assert.That(logOnly.ImplementedUnionNames).IsEquivalentTo(["IEventLog"]);
        await Assert.That(logOnly.SessionScopedEvent).IsNotNull();
        await Assert.That(plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Qualified").ImplementedUnionNames)
            .IsEquivalentTo(["IEvent", "IEventLog"]);
    }

    [Test]
    [Arguments("Plain")]
    [Arguments("OptionalId")]
    [Arguments("Nested")]
    [Arguments("NotAnEvent")]
    public async Task Bind_Should_Leave_A_Non_Qualifying_Record_Untouched(string name)
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), SessionScopedEventScenario.EventCuration());

        await Assert.That(plan.Models.OfType<ObjectModelPlan>().Single(model => model.Name == name).SessionScopedEvent).IsNull();
    }

    [Test]
    [Arguments("missing", "identifier schema does not exist")]
    [Arguments("missing-union", "not a generated marked union")]
    [Arguments("empty", "no event qualifies")]
    [Arguments("reason", "must declare a reason")]
    public async Task Bind_Should_Refuse_An_Invalid_Curation_Row(string fault, string problem)
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario());
        var row = SessionScopedEventScenario.Rule();
        row = fault switch
        {
            "missing" => row with { Identifier = "Missing.ID" },
            "missing-union" => row with { Unions = ["IMissingEvent"] },
            "empty" => row with { Member = "missingID" },
            "reason" => row with { Reason = string.Empty },
            _ => throw new ArgumentException("Unknown scenario fault.", nameof(fault)),
        };

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(
            document, Selection("event.get"), SessionScopedEventScenario.EventCuration(row)));

        await Assert.That(exception.Errors.Any(error => error.Problem.Contains(problem, StringComparison.Ordinal)))
            .IsTrue().Because(string.Join(" | ", exception.Errors.Select(static error => error.Problem)));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Qualifying_Payload_That_Can_Be_Absent()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario(optionalPayload: true));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(
            document, Selection("event.get"), SessionScopedEventScenario.EventCuration()));

        await Assert.That(exception.Errors.Any(static error => error.Subject == "Qualified"
            && error.Problem.Contains("payload must be required and non-nullable", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>A payload the wire may send as null cannot answer the contract, even when it is required.</summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Required_Payload_That_Can_Be_Null()
    {
        var document = await BindingTestHost.IngestAsync(new SessionScopedEventScenario(nullablePayload: true));

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(
            document, Selection("event.get"), SessionScopedEventScenario.EventCuration()));

        await Assert.That(exception.Errors.Any(static error => error.Subject == "Qualified"
            && error.Problem.Contains("payload must be required and non-nullable", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Take_The_Union_And_Member_Identities_From_Curation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("Conversation.ID", static schema => schema.Type("string"))
            .WithSchema("Notice", static schema => schema.Type("object")
                .Property("type", static property => property.Type("string").Const("notice"), required: true)
                .Property("payload", static payload => payload.Type("object")
                    .Property("ownerID", static member => member.Ref("Conversation.ID"), required: true), required: true))
            .WithSchema("Other", static schema => schema.Type("object")
                .Property("type", static property => property.Type("string").Const("other"), required: true))
            .WithSchema("NoticeFamily", static schema => schema.AnyOf(static arm => arm.Ref("Notice"), static arm => arm.Ref("Other")))
            .WithSchema("Holder", static schema => schema.Type("object")
                .Property("notice", static property => property.Ref("NoticeFamily"), required: true))
            .WithOperation("notice.get", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Holder")))));
        var row = SessionScopedEventScenario.Rule() with
        {
            Unions = ["INoticeFamily"],
            PayloadMember = "payload",
            Member = "ownerID",
            Identifier = "Conversation.ID",
        };
        var plan = new BindingTestHost().Bind(document, Selection("notice.get"),
            Curation(Groups("notice", RootGroup()), sessionScopedEvent: row));

        var projection = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Notice").SessionScopedEvent;
        await Assert.That(projection).IsNotNull();
        await Assert.That(projection!.PayloadPropertyName).IsEqualTo("Payload");
        await Assert.That(projection.IdentifierPropertyName).IsEqualTo("OwnerId");
        await Assert.That(projection.MemberName).IsEqualTo("SessionId");
    }

    [Test]
    public async Task Bind_Should_Exclude_The_Pinned_Form_Events()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();
        var events = plan.Models.OfType<ObjectModelPlan>()
            .Where(static model => model.Properties.Any(static property => property.WireName == "type"
                && property.LiteralValue is "form.cancelled" or "form.replied" or "form.created"))
            .ToArray();

        await Assert.That(events.Select(static model => model.Properties.Single(static property => property.WireName == "type").LiteralValue ?? string.Empty))
            .IsEquivalentTo(["form.cancelled", "form.replied", "form.created"]);
        await Assert.That(events.All(static model => model.SessionScopedEvent is null)).IsTrue();
    }

    [Test]
    [Arguments("session.message.content.updated")]
    [Arguments("session.usage.recorded")]
    [Arguments("permission.asked")]
    [Arguments("permission.replied")]
    [Arguments("persistent-pty.added")]
    [Arguments("persistent-pty.removed")]
    [Arguments("tui.session.select")]
    public async Task Bind_Should_Project_The_Pinned_Log_Only_And_Non_Session_Family_Events(string wireType)
    {
        var plan = await new BindingTestHost().BindPinnedAsync();
        var model = plan.Models.OfType<ObjectModelPlan>().Single(model => model.Properties
            .Any(property => property.WireName == "type" && property.LiteralValue == wireType));

        await Assert.That(model.SessionScopedEvent).IsNotNull();
    }
}
