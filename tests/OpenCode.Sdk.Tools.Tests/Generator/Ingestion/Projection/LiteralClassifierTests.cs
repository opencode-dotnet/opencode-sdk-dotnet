using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;
using OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Projection;

public sealed class LiteralClassifierTests
{
    [Test]
    public async Task Project_Should_Produce_String_Literal_From_Single_Value_Enum()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Kind", schema => schema.Type("string").Enum("created")));

        var result = await host.ProjectAsync(scenario);

        var literal = (LiteralNode)result.Schemas["Kind"];
        await Assert.That(literal.Kind).IsEqualTo(LiteralKind.String);
        await Assert.That(literal.Value).IsEqualTo("created");
        await Assert.That(literal.Dialect).IsEqualTo(LiteralDialect.SingleValueEnum);
    }

    [Test]
    public async Task Project_Should_Produce_Boolean_Literal_From_Single_Value_Enum()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Healthy", schema => schema.Type("boolean").BooleanEnum(true)));

        var result = await host.ProjectAsync(scenario);

        var literal = (LiteralNode)result.Schemas["Healthy"];
        await Assert.That(literal.Kind).IsEqualTo(LiteralKind.Boolean);
        await Assert.That(literal.Value).IsEqualTo("true");
        await Assert.That(literal.Dialect).IsEqualTo(LiteralDialect.SingleValueEnum);
    }

    [Test]
    public async Task Project_Should_Produce_String_Literal_From_Const()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Kind", schema => schema.Type("string").Const("updated")));

        var result = await host.ProjectAsync(scenario);

        var literal = (LiteralNode)result.Schemas["Kind"];
        await Assert.That(literal.Kind).IsEqualTo(LiteralKind.String);
        await Assert.That(literal.Value).IsEqualTo("updated");
        await Assert.That(literal.Dialect).IsEqualTo(LiteralDialect.Const);
    }

    [Test]
    public async Task Project_Should_Refuse_Const_On_Non_String_Schema()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.Type("number").Const("1")));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("const");
        await Assert.That(exception.Message).Contains("string");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Enum_And_Const_Are_Combined()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.Type("string").Enum("one").Const("one")));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("enum");
        await Assert.That(exception.Message).Contains("const");
    }

    [Test]
    public async Task Project_Should_Refuse_Multi_Value_Boolean_Enum()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.Type("boolean").BooleanEnum(true, false)));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("boolean");
        await Assert.That(exception.Message).Contains("multiple");
    }

    [Test]
    public async Task Project_Should_Collect_Only_Required_Literal_Markers()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Event", schema => schema
            .Type("object")
            .Property("type", property => property.Type("string").Enum("created"), required: true)
            .Property("healthy", property => property.Type("boolean").BooleanEnum(true))
            .Property("data", property => property.Type("string"), required: true)
            .Property("success", property => property.Type("boolean").BooleanEnum(false), required: true)));

        var result = await host.ProjectAsync(scenario);

        var markers = ((ObjectNode)result.Schemas["Event"]).LiteralMarkers;
        await Assert.That(markers).Count().IsEqualTo(2);
        await Assert.That(markers[0].PropertyName).IsEqualTo("type");
        await Assert.That(markers[0].Kind).IsEqualTo(LiteralKind.String);
        await Assert.That(markers[0].Value).IsEqualTo("created");
        await Assert.That(markers[1].PropertyName).IsEqualTo("success");
        await Assert.That(markers[1].Kind).IsEqualTo(LiteralKind.Boolean);
        await Assert.That(markers[1].Value).IsEqualTo("false");
    }

    [Test]
    public async Task FindFirstPrefixMarker_Should_Return_The_First_Required_Prefix_And_Ignore_Literals()
    {
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Event", schema => schema
            .Type("object")
            .Property("kind", property => property.Type("string").Enum("fixed"), required: true)
            .Property("type", property => property.Type("string").Pattern("^rpc\\.[\\s\\S]*?$"), required: true)
            .Property("later", property => property.Type("string").Pattern("^x\\.[\\s\\S]*?$"))));
        var schema = await SchemaProjectionTestHost.LoadSchemaAsync(scenario, "Event");

        var marker = LiteralClassifier.FindFirstPrefixMarker(schema);

        await Assert.That(marker).IsNotNull();
        await Assert.That(marker.PropertyName).IsEqualTo("type");
        await Assert.That(marker.Prefix).IsEqualTo("rpc.");
    }
}
