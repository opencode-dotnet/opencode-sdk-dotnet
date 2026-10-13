using System.Text.Json.Nodes;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal sealed class StreamOperationScenario(
    StreamFrameProfile frameProfile = StreamFrameProfile.Valid,
    StreamExtensionProfile extensionProfile = StreamExtensionProfile.Valid,
    bool carriesRequestBody = false) : SpecScenario
{
    public const string OperationId = "example.events";
    public const string GroupName = "example";
    public const string FailureEventName = "effect/http-api/stream/failure";

    protected override void Arrange(SpecDocumentBuilder spec)
    {
        _ = spec
            .WithSchema("ExampleEvent", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("ExampleEventJsonString", schema => schema
                .Type("string")
                .Raw("contentMediaType", "\"application/json\"")
                .Raw("contentSchema", "{\"$ref\":\"#/components/schemas/ExampleEvent\"}"))
            .WithSchema("ExampleEventFrame", schema => schema
                .Type("object")
                .Property("id", ConfigureId, required: frameProfile is not StreamFrameProfile.OptionalId)
                .Property("event", ConfigureEvent, required: frameProfile is not StreamFrameProfile.OptionalEvent)
                .Property("data", property => property.Ref("ExampleEventJsonString"), required: frameProfile is not StreamFrameProfile.OptionalData))
            .WithSchema("ExampleEventsPostRequest", schema => schema
                .Type("object")
                .Property("filter", property => property.Type("string"), required: true))
            .WithSchema("ExampleBadRequestError", schema => Error(schema, "ExampleBadRequestError"))
            .WithSchema("ExampleUnauthorizedError", schema => Error(schema, "ExampleUnauthorizedError"))
            .WithSchema("ExampleNotFoundError", schema => Error(schema, "ExampleNotFoundError"))
            .WithSchema("ExampleGoneError", schema => Error(schema, "ExampleGoneError"))
            .WithOperation(
                OperationId,
                method: carriesRequestBody ? "post" : "get",
                path: "/api/example/{exampleID}/events",
                configure: operation => ConfigureOperation(operation, carriesRequestBody));
    }

    private void ConfigureId(SchemaBuilder schema)
    {
        _ = frameProfile switch
        {
            StreamFrameProfile.Valid or StreamFrameProfile.OptionalId or StreamFrameProfile.OptionalEvent or StreamFrameProfile.OptionalData => schema.AnyOf(
                static branch => branch.Type("string"),
                static branch => branch.Type("null")),
            StreamFrameProfile.NonNullableStringId => schema.Type("string"),
            StreamFrameProfile.NullableNumberId => schema.AnyOf(
                static branch => branch.Type("number"),
                static branch => branch.Type("null")),
            StreamFrameProfile.NumberEvent => schema.AnyOf(
                static branch => branch.Type("string"),
                static branch => branch.Type("null")),
            _ => throw new InvalidOperationException($"Unknown stream-frame profile '{frameProfile}'."),
        };
    }

    private void ConfigureEvent(SchemaBuilder schema)
    {
        _ = frameProfile is StreamFrameProfile.NumberEvent
            ? schema.Type("number")
            : schema.Type("string");
    }

    private void ConfigureOperation(OperationBuilder operation, bool withBody)
    {
        _ = operation
            .Parameter("exampleID", "path", schema => schema.Type("string"), required: true)
            .Summary("Watch example events")
            .Description("The example stream is volatile by contract.");
        if (withBody)
        {
            _ = operation.RequestBody(
                "application/json",
                schema => schema.Ref("ExampleEventsPostRequest"),
                required: true);
        }

        _ = operation
            .SseResponse(schema => schema.Ref("ExampleEventFrame"), EffectStreamJson())
            .Response(400, "application/json", schema => schema.Ref("ExampleBadRequestError"))
            .Response(401, "application/json", schema => schema.Ref("ExampleUnauthorizedError"))
            .Response(404, "application/json", schema => schema.AnyOf(
                static branch => branch.Ref("ExampleNotFoundError"),
                static branch => branch.Ref("ExampleGoneError")));
    }

    private string EffectStreamJson()
    {
        if (extensionProfile is StreamExtensionProfile.NonObject)
        {
            return "[]";
        }

        var extension = JsonNode.Parse(new FixtureLoader().Load("effect-stream.json"))!.AsObject();
        switch (extensionProfile)
        {
            case StreamExtensionProfile.Valid:
                break;
            case StreamExtensionProfile.MissingEncoding:
                _ = extension.Remove("encoding");
                break;
            case StreamExtensionProfile.UnsupportedEncoding:
                extension["encoding"] = "jsonl";
                break;
            case StreamExtensionProfile.MessageFailure:
                extension["failureEvent"] = "message";
                break;
            case StreamExtensionProfile.MissingCauseSchema:
                _ = extension.Remove("causeSchema");
                break;
            case StreamExtensionProfile.NonNeverErrorSchema:
                extension["errorSchema"] = new JsonObject
                {
                    ["type"] = "string",
                };
                break;
            case StreamExtensionProfile.MissingErrorSchema:
                _ = extension.Remove("errorSchema");
                break;
            default:
                throw new InvalidOperationException($"Unknown stream-extension profile '{extensionProfile}'.");
        }

        return extension.ToJsonString();
    }

    private static void Error(SchemaBuilder schema, string tag) => schema
        .Type("object")
        .Property("_tag", property => property.Type("string").Enum(tag), required: true)
        .Property("message", property => property.Type("string"), required: true);
}
