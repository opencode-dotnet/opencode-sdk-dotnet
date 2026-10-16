using System.Text.Json.Nodes;

namespace OpenCode.Sdk.Tools.Tests.Support;

public sealed class SpecDocumentBuilderTests
{
    [Test]
    public async Task WithRawSchema_Should_Report_Missing_Embedded_Fixture()
    {
        var builder = new SpecDocumentBuilder(new FixtureLoader());

        var exception = await Assert
            .That(() => builder.WithRawSchema("Missing", "no-such-schema.json"))
            .Throws<ArgumentException>();
        var message = exception?.Message ?? throw new InvalidOperationException("The assertion did not return the exception.");

        await Assert.That(message).Contains("no-such-schema.json");
    }

    [Test]
    public async Task WithRawTopLevelFromFixture_Should_Report_Missing_Embedded_Fixture()
    {
        var builder = new SpecDocumentBuilder(new FixtureLoader());

        var exception = await Assert
            .That(() => builder.WithRawTopLevelFromFixture("webhooks", "no-such-wall.json"))
            .Throws<ArgumentException>();
        var message = exception?.Message ?? throw new InvalidOperationException("The assertion did not return the exception.");

        await Assert.That(message).Contains("no-such-wall.json");
    }

    [Test]
    public async Task BuildJson_Should_Apply_Document_Level_Overrides()
    {
        var json = new SpecDocumentBuilder()
            .WithOpenApiVersion("3.2.0")
            .WithRawTopLevel("x-document", "true")
            .BuildJson();

        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("The builder returned the JSON null literal.");

        await Assert.That(root["openapi"]?.GetValue<string>()).IsEqualTo("3.2.0");
        await Assert.That(root["x-document"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task BuildJson_Should_Produce_Document_With_Schema_And_Operation()
    {
        var json = new SpecDocumentBuilder()
            .WithSchema(
                "Session",
                schema => schema
                    .Type("object")
                    .Property("id", property => property.Type("string"), required: true)
                    .AdditionalPropertiesFalse())
            .WithOperation(
                "session.get",
                method: "get",
                path: "/api/session/{sessionID}",
                configure: operation => operation
                    .Parameter(
                        "sessionID",
                        "path",
                        schema => schema.Type("string"),
                        required: true)
                    .Response(200, "application/json", schema => schema.Ref("Session")))
            .BuildJson();

        var root = JsonNode.Parse(json)
                   ?? throw new InvalidOperationException("The builder returned the JSON null literal.");

        await Assert.That(root["openapi"]!.GetValue<string>()).IsEqualTo("3.1.0");
        await Assert
            .That(root["components"]!["schemas"]!["Session"]!["required"]!.AsArray())
            .Count()
            .IsEqualTo(1);
        await Assert
            .That(root["paths"]!["/api/session/{sessionID}"]!["get"]!["operationId"]!.GetValue<string>())
            .IsEqualTo("session.get");
    }

    [Test]
    public async Task BuildJson_Should_Produce_Composed_And_Collection_Schema_Keywords()
    {
        var json = new SpecDocumentBuilder()
            .WithSchema(
                "Composite",
                schema => schema
                    .Type("object")
                    .Property(
                        "kind",
                        property => property
                            .Type("string")
                            .Enum("one", "two")
                            .Const("one"))
                    .Property(
                        "values",
                        property => property
                            .Type("array")
                            .Items(item => item.Type("string"))
                            .PrefixItems(
                                item => item.Type("string"),
                                item => item.Type("number")))
                    .Property(
                        "choice",
                        property => property
                            .AnyOf(
                                branch => branch.Type("string"),
                                branch => branch.Type("number"))
                            .OneOf(branch => branch.Type("boolean"))
                            .AllOf(branch => branch.Ref("Shared")))
                    .Required("kind")
                    .AdditionalProperties(value => value.Type("integer")))
            .BuildJson();

        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("The builder returned the JSON null literal.");
        var schema = root["components"]!["schemas"]!["Composite"]!;

        await Assert.That(schema["required"]![0]!.GetValue<string>()).IsEqualTo("kind");
        await Assert
            .That(schema["properties"]!["kind"]!["enum"]!.AsArray().Count)
            .IsEqualTo(2);
        await Assert
            .That(schema["properties"]!["kind"]!["const"]!.GetValue<string>())
            .IsEqualTo("one");
        await Assert
            .That(schema["properties"]!["values"]!["items"]!["type"]!.GetValue<string>())
            .IsEqualTo("string");
        await Assert
            .That(schema["properties"]!["values"]!["prefixItems"]!.AsArray().Count)
            .IsEqualTo(2);
        await Assert
            .That(schema["properties"]!["choice"]!["anyOf"]!.AsArray().Count)
            .IsEqualTo(2);
        await Assert
            .That(schema["properties"]!["choice"]!["oneOf"]!.AsArray().Count)
            .IsEqualTo(1);
        await Assert
            .That(schema["properties"]!["choice"]!["allOf"]!.AsArray().Count)
            .IsEqualTo(1);
        await Assert
            .That(schema["additionalProperties"]!["type"]!.GetValue<string>())
            .IsEqualTo("integer");
    }

    [Test]
    public async Task BuildJson_Should_Produce_Specialized_Schema_Keywords()
    {
        var json = new SpecDocumentBuilder()
            .WithSchema(
                "Specialized",
                schema => schema
                    .Type("object")
                    .Description("Specialized schema")
                    .Format("custom")
                    .PatternProperties("^x-", value => value.Type("string"))
                    .ContentSchema("application/json", content => content.Type("object"))
                    .Property("anything", property => property.Unrestricted())
                    .Raw("x-test-keyword", "true"))
            .BuildJson();

        var root = JsonNode.Parse(json)
                   ?? throw new InvalidOperationException("The builder returned the JSON null literal.");
        var schema = root["components"]!["schemas"]!["Specialized"]!;

        await Assert
            .That(schema["description"]!.GetValue<string>())
            .IsEqualTo("Specialized schema");
        await Assert.That(schema["format"]!.GetValue<string>()).IsEqualTo("custom");
        await Assert
            .That(schema["patternProperties"]!["^x-"]!["type"]!.GetValue<string>())
            .IsEqualTo("string");
        await Assert
            .That(schema["contentMediaType"]!.GetValue<string>())
            .IsEqualTo("application/json");
        await Assert
            .That(schema["contentSchema"]!["type"]!.GetValue<string>())
            .IsEqualTo("object");
        await Assert.That(schema["properties"]!["anything"]!.AsObject().Count).IsEqualTo(0);
        await Assert.That(schema["x-test-keyword"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task BuildJson_Should_Produce_Operation_Request_Response_And_Metadata()
    {
        var json = new SpecDocumentBuilder()
            .WithOperation(
                "events.subscribe",
                method: "post",
                path: "/api/events",
                configure: operation => operation
                    .Parameter(
                        "filter",
                        "query",
                        schema => schema.Type("object"),
                        deepObject: true)
                    .RequestBody(
                        "application/json",
                        schema => schema.Ref("Subscription"),
                        required: true)
                    .Response(204)
                    .SseResponse(
                        schema => schema.Ref("Event"),
                        effectStreamJson: "{\"encoding\":\"sse\"}")
                    .Extension("x-operation", "true")
                    .Raw("tags", "[\"events\"]")
                    .Deprecated()
                    .Summary("Subscribe to events"))
            .BuildJson();

        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("The builder returned the JSON null literal.");
        var operation = root["paths"]!["/api/events"]!["post"]!;
        var parameter = operation["parameters"]![0]!;
        var requestBody = operation["requestBody"]!;
        var eventStream = operation["responses"]!["200"]!["content"]!["text/event-stream"]!;

        await Assert.That(parameter["style"]!.GetValue<string>()).IsEqualTo("deepObject");
        await Assert.That(parameter["explode"]!.GetValue<bool>()).IsTrue();
        await Assert.That(requestBody["required"]!.GetValue<bool>()).IsTrue();
        await Assert
            .That(requestBody["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>())
            .IsEqualTo("#/components/schemas/Subscription");
        await Assert.That(operation["responses"]!["204"]).IsNotNull();
        await Assert.That(eventStream["schema"]!["$ref"]!.GetValue<string>()).IsEqualTo("#/components/schemas/Event");
        await Assert.That(eventStream["x-effect-stream"]!["encoding"]!.GetValue<string>()).IsEqualTo("sse");
        await Assert.That(operation["x-operation"]!.GetValue<bool>()).IsTrue();
        await Assert.That(operation["tags"]![0]!.GetValue<string>()).IsEqualTo("events");
        await Assert.That(operation["deprecated"]!.GetValue<bool>()).IsTrue();
        await Assert.That(operation["summary"]!.GetValue<string>()).IsEqualTo("Subscribe to events");
    }
}
