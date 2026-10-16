using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Walls;

public sealed class SchemaWallPolicyTests
{
    [Test]
    public async Task Project_Should_Refuse_When_Schema_Uses_AllOf()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.AllOf(branch => branch.Type("string"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Type_Is_An_Array()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.Raw("type", "[\"string\",\"null\"]")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("type");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Schema_Has_Unknown_Raw_Keyword()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.Raw("madeUpKeyword", "true")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("madeUpKeyword");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Ignore_Known_Validation_Keywords()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema
            .Type("string")
            .Raw("pattern", "\"^[a-z]+$\"")
            .Raw("minimum", "0")
            .Raw("maximum", "10")
            .Raw("exclusiveMinimum", "1")
            .Raw("minItems", "1")
            .Raw("maxItems", "5")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
    }

    [Test]
    public async Task Project_Should_Ignore_Typed_Annotation_And_Validation_Members()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema
            .Type("string")
            .Raw("title", "\"a title\"")
            .Raw("default", "\"fallback\"")
            .Raw("readOnly", "true")
            .Raw("minLength", "1")
            .Raw("x-custom", "true")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
    }

    [Test]
    public async Task Project_Should_Refuse_Unsupported_Not_At_The_Applicator_Location()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Raw("not", "{\"type\":\"string\"}")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Errors).Count().IsEqualTo(1);
        await Assert.That(ex.Errors[0].Location).IsEqualTo("Bad/not");
        await Assert.That(ex.Errors[0].Problem).Contains("not");
    }

    [Test]
    public async Task Project_Should_Refuse_Not_Null_When_Combined_With_Another_Shape()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("object").Raw("not", "{\"type\":\"null\"}")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Errors).Count().IsEqualTo(1);
        await Assert.That(ex.Errors[0].Location).IsEqualTo("Bad/not");
    }

    [Test]
    public async Task Project_Should_Refuse_Empty_Not_When_Combined_With_Another_Shape()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("string").Raw("not", "{}")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Errors).Count().IsEqualTo(1);
        await Assert.That(ex.Errors[0].Location).IsEqualTo("Bad/not");
    }
}
