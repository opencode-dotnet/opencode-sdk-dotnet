using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;

internal sealed class EventDocumentationScenario(bool sharedPayload = false, bool payloadElsewhere = false, string? description = null,
    bool carriesBody = false, bool payloadAsBody = false) : SpecScenario
{
    protected override void Arrange(SpecDocumentBuilder spec)
    {
        spec.WithSchema("Unselected", static schema => schema.Type("object")
            .Property("text", static property => property.Type("string")));
        spec.WithSchema("Details", static schema => schema.Type("object")
            .Property("text", static property => property.Type("string"), required: true));
        spec.WithSchema("Notice", schema =>
        {
            schema.Type("object")
                .Property("type", static property => property.Type("string").Const("notice.changed"), required: true)
                .Property("data", static property => property.Ref("Details"), required: true);
            if (payloadElsewhere)
            {
                schema.Property("backup", static property => property.Ref("Details"), required: true);
            }
            if (description is not null)
            {
                schema.Description(description);
            }
        });
        spec.WithSchema("Other", schema =>
        {
            schema.Type("object").Property("type", static property => property.Type("string").Const("other"), required: true);
            if (sharedPayload)
            {
                schema.Property("data", static property => property.Ref("Details"), required: true);
            }
        });
        spec.WithSchema("Events", static schema => schema.AnyOf(static arm => arm.Ref("Notice"), static arm => arm.Ref("Other")));
        spec.WithSchema("Frame", static schema => schema.Type("object")
            .Property("id", static property => property.AnyOf(static arm => arm.Type("string"), static arm => arm.Type("null")))
            .Property("event", static property => property.Type("string"), required: true)
            .Property("data", static property => property.Type("string").ContentSchema("application/json", static inner => inner.Ref("Events")), required: true));
        spec.WithOperation("event.get", configure: static operation =>
            operation.SseResponseFromFixture(static schema => schema.Ref("Frame"), "effect-stream.json"));
        if (payloadAsBody)
        {
            // The event's payload is also the body of an ordinary call, so it has two wire roles.
            spec.WithOperation("event.create", method: "post", configure: static operation => operation
                .RequestBody("application/json", static schema => schema.Ref("Details"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")));
        }

        if (carriesBody)
        {
            // The same record is also the body of an ordinary call, so it has two wire roles.
            spec.WithOperation("event.create", method: "post", configure: static operation => operation
                .RequestBody("application/json", static schema => schema.Ref("Notice"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")));
        }
    }
}
