using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;

internal sealed class SessionScopedEventScenario(bool optionalPayload = false) : SpecScenario
{
    public static GenerationCuration EventCuration(SessionScopedEventCuration? row = null) =>
        Curation(Groups("event", RootGroup()), sessionScopedEvent: row ?? Rule());

    public static SessionScopedEventCuration Rule() => new()
    {
        Unions = ["IEvent", "IEventLog"],
        Interface = "ISessionScopedEvent",
        InterfaceMember = "SessionId",
        PayloadMember = "data",
        Member = "sessionID",
        Identifier = "Session.ID",
        Reason = "The event payload names the session the event belongs to.",
    };

    protected override void Arrange(SpecDocumentBuilder spec)
    {
        spec.WithSchema("Session.ID", static schema => schema.Type("string").Pattern("^ses"))
            .WithSchema("Session.ID_1", static schema => schema.Type("string").Pattern("^ses").Description("Session to select"))
            .WithSchema("Qualified", schema => schema.Type("object")
                .Property("type", static member => member.Type("string").Const("qualified"), required: true)
                .Property("data", static data => data.Type("object")
                    .Property("sessionID", static id => id.Ref("Session.ID"), required: true), required: !optionalPayload))
            .WithSchema("Duplicate", static schema => schema.Type("object")
                .Property("type", static member => member.Type("string").Const("duplicate"), required: true)
                .Property("data", static data => data.Type("object")
                    .Property("sessionID", static id => id.Ref("Session.ID_1"), required: true), required: true))
            .WithSchema("Plain", static schema => schema.Type("object")
                .Property("type", static member => member.Type("string").Const("plain"), required: true)
                .Property("data", static data => data.Type("object")
                    .Property("sessionID", static id => id.Type("string"), required: true), required: true))
            .WithSchema("OptionalId", static schema => schema.Type("object")
                .Property("type", static member => member.Type("string").Const("optional"), required: true)
                .Property("data", static data => data.Type("object")
                    .Property("sessionID", static id => id.Ref("Session.ID")), required: true))
            .WithSchema("Nested", static schema => schema.Type("object")
                .Property("type", static member => member.Type("string").Const("nested"), required: true)
                .Property("data", static data => data.Type("object")
                    .Property("form", static form => form.Type("object")
                        .Property("sessionID", static id => id.Ref("Session.ID"), required: true), required: true), required: true))
            .WithSchema("NotAnEvent", static schema => schema.Type("object")
                .Property("data", static data => data.Type("object")
                    .Property("sessionID", static id => id.Ref("Session.ID"), required: true), required: true))
            .WithSchema("LogOnly", static schema => schema.Type("object")
                .Property("type", static member => member.Type("string").Const("log-only"), required: true)
                .Property("data", static data => data.Type("object")
                    .Property("sessionID", static id => id.Ref("Session.ID"), required: true), required: true))
            .WithSchema("Event", static schema => schema.AnyOf(
                static arm => arm.Ref("Qualified"), static arm => arm.Ref("Duplicate"),
                static arm => arm.Ref("Plain"), static arm => arm.Ref("OptionalId"), static arm => arm.Ref("Nested")))
            .WithSchema("EventLog", static schema => schema.AnyOf(
                static arm => arm.Ref("Qualified"), static arm => arm.Ref("LogOnly")))
            .WithSchema("Holder", static schema => schema.Type("object")
                .Property("event", static member => member.Ref("Event"), required: true)
                .Property("log", static member => member.Ref("EventLog"), required: true)
                .Property("other", static member => member.Ref("NotAnEvent"), required: true))
            .WithOperation("event.get", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Holder")));
    }
}
