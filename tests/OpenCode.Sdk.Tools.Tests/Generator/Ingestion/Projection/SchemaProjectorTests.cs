using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;
using OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Projection;

public sealed class SchemaProjectorTests
{
    [Test]
    public async Task Project_Should_Produce_String_Primitive_Node()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Type("string")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)result.Schemas["Value"]).Kind).IsEqualTo(PrimitiveKind.String);
    }

    [Test]
    public async Task Project_Should_Produce_Number_Primitive_Node()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Type("number")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)result.Schemas["Value"]).Kind).IsEqualTo(PrimitiveKind.Number);
    }

    [Test]
    public async Task Project_Should_Produce_Integer_Primitive_Node()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Type("integer")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)result.Schemas["Value"]).Kind).IsEqualTo(PrimitiveKind.Integer);
    }

    [Test]
    public async Task Project_Should_Produce_Boolean_Primitive_Node()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Type("boolean")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)result.Schemas["Value"]).Kind).IsEqualTo(PrimitiveKind.Boolean);
    }

    [Test]
    public async Task Project_Should_Produce_Encoded_String_Node()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Checkpoint", schema => schema
            .Type("string")
            .Format("byte")
            .Raw("contentEncoding", "\"base64\"")));

        var result = await host.ProjectAsync(scenario);

        var node = (EncodedStringNode)result.Schemas["Checkpoint"];
        await Assert.That(node.ContentEncoding).IsEqualTo("base64");
        await Assert.That(node.Format).IsEqualTo("byte");
    }

    [Test]
    public async Task Project_Should_Refuse_Content_Encoding_On_A_Non_String()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Checkpoint", schema => schema
            .Type("integer")
            .Raw("contentEncoding", "\"base64\"")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("contentEncoding");
        await Assert.That(ex.Message).Contains("do not form a supported core shape");
    }

    [Test]
    public async Task Project_Should_Record_Format()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Type("string").Format("uuid")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"].Format).IsEqualTo("uuid");
    }

    [Test]
    public async Task Project_Should_Record_Description()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Type("string").Description("A value.")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"].Description).IsEqualTo("A value.");
    }

    [Test]
    public async Task Project_Should_Produce_Unrestricted_Node_For_Description_Only_Schema()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("ToolResult", schema => schema.Description("Any result.")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["ToolResult"]).IsTypeOf<UnrestrictedNode>();
        await Assert.That(result.Schemas["ToolResult"].Description).IsEqualTo("Any result.");
    }

    [Test]
    public async Task Project_Should_Produce_Unrestricted_Node_For_Empty_Schema()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("ToolResult", schema => schema.Unrestricted()));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["ToolResult"]).IsTypeOf<UnrestrictedNode>();
    }

    [Test]
    public async Task Project_Should_Preserve_Empty_Not_As_Never()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Raw("not", "{}")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<NeverNode>();
    }

    [Test]
    public async Task Project_Should_Read_Not_Null_As_A_Struct_With_No_Declared_Fields()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Raw("not", "{\"type\":\"null\"}")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<FreeFormObjectNode>();
    }

    [Test]
    public async Task Project_Should_Keep_Dotted_Schema_Name_Verbatim()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("session.info", schema => schema.Type("string")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas).ContainsKey("session.info");
    }

    [Test]
    public async Task Project_Should_Produce_Enum_Node_For_Multiple_String_Values()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Mode", schema => schema.Type("string").Enum("fast", "safe")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Mode"]).IsTypeOf<EnumNode>();
        var node = (EnumNode)result.Schemas["Mode"];
        await Assert.That(node.Values).Count().IsEqualTo(2);
        await Assert.That(node.Values[0]).IsEqualTo("fast");
        await Assert.That(node.Values[1]).IsEqualTo("safe");
    }

    [Test]
    public async Task Project_Should_Produce_Array_Node_With_Item()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Values", schema => schema.Type("array").Items(item => item.Type("integer"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Values"]).IsTypeOf<ArrayNode>();
        var item = ((ArrayNode)result.Schemas["Values"]).Item;
        await Assert.That(item).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)item).Kind).IsEqualTo(PrimitiveKind.Integer);
    }

    [Test]
    public async Task Project_Should_Promote_Inline_Enum_Under_Items()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec =>
            spec.WithSchema("Modes", schema => schema.Type("array").Items(item => item.Type("string").Enum("fast", "safe"))));

        var result = await host.ProjectAsync(scenario);

        var item = ((ArrayNode)result.Schemas["Modes"]).Item;
        await Assert.That(item).IsTypeOf<RefNode>();
        await Assert.That(((RefNode)item).Target).IsEqualTo("Modes#/items");
        await Assert.That(result.Schemas["Modes#/items"]).IsTypeOf<EnumNode>();
    }

    [Test]
    public async Task Project_Should_Produce_Ref_Node_For_Existing_Target()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("Target", schema => schema.Type("string"))
            .WithSchema("Alias", schema => schema.Ref("Target")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Alias"]).IsTypeOf<RefNode>();
        await Assert.That(((RefNode)result.Schemas["Alias"]).Target).IsEqualTo("Target");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Ref_Target_Is_Missing()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.Ref("Missing")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("Bad");
        await Assert.That(ex.Message).Contains("Missing");
    }

    [Test]
    public async Task Project_Should_Produce_Array_Of_Any_Value_When_Items_Are_Absent()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.Type("array")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<ArrayNode>();
        await Assert.That(((ArrayNode)result.Schemas["Value"]).Item).IsTypeOf<UnrestrictedNode>();
    }

    [Test]
    public async Task Project_Should_Refuse_When_Primitive_Has_OneOf_Constraint()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.Type("string").OneOf(branch => branch.Type("number"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("oneOf");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Enum_Has_Object_Constraint()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("string")
            .Enum("one", "two")
            .Property("value", property => property.Type("string"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("properties");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Array_Has_Union_Constraint()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("array")
            .Items(item => item.Type("string"))
            .AnyOf(branch => branch.Type("string"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("anyOf");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Batch_Errors_From_Multiple_Schemas()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("BadAllOf", schema => schema
                .Type("string")
                .AllOf(first => first.Raw("pattern", "\"^a\""), second => second.Raw("minLength", "1")))
            .WithSchema("BadKeyword", schema => schema.Raw("madeUpKeyword", "true")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Errors).Count().IsEqualTo(2);
        await Assert.That(ex.Message).Contains("BadAllOf");
        await Assert.That(ex.Message).Contains("BadKeyword");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Promoted_Graph_Key_Collides()
    {
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("First", schema => schema.Type("string").Enum("fast", "safe"))
            .WithSchema("Second", schema => schema.Type("string").Enum("on", "off")));
        var context = scenario.Build();
        var errors = new IngestionErrorCollector();
        var loaded = await new SpecReader(context.FileSystem).LoadAsync(context.SpecPath, errors, CancellationToken.None);
        var keys = new GraphKeyBuilder();
        var projector = new SchemaProjector(keys);
        var state = new ProjectionState(errors, loaded.Document);

        _ = projector.Project(loaded.Document.Components!.Schemas!["First"], "Shared", "/value", state);
        _ = projector.Project(loaded.Document.Components.Schemas["Second"], "Shared", "/value", state);
        var ex = await Assert.That(errors.ThrowIfAny).Throws<IngestionException>();

        await Assert.That(ex!.Message).Contains("collision");
        await Assert.That(ex.Message).Contains("Shared#/value");
    }

    [Test]
    public async Task Project_Should_Unwrap_Validation_Only_AllOf_Wrapper()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Value",
            schema => schema.Type("integer").AllOf(element => element.Raw("exclusiveMinimum", "0"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)result.Schemas["Value"]).Kind).IsEqualTo(PrimitiveKind.Integer);
    }

    [Test]
    public async Task Project_Should_Unwrap_Validation_Only_AllOf_Wrapper_When_Annotated()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Value",
            schema => schema
                .Type("string")
                .AllOf(element => element.Raw("pattern", "\"^ses_\"").Description("session identifier"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Value"]).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)result.Schemas["Value"]).Kind).IsEqualTo(PrimitiveKind.String);
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_When_It_Has_Multiple_Elements()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema
                .Type("string")
                .AllOf(first => first.Raw("pattern", "\"^a\""), second => second.Raw("minLength", "1"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_Wrapper_When_Element_Has_Structural_Member()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("string").AllOf(element => element.Type("string"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_Wrapper_When_Element_Has_Format()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("string").AllOf(element => element.Format("uuid"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_Wrapper_When_Element_Has_Example()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("string").AllOf(element => element.Raw("example", "\"ses_1\""))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_Wrapper_When_Element_Has_Unrecognized_Keyword()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("string").AllOf(element => element.Raw("customKeyword", "true"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_Wrapper_When_Element_Is_A_Reference()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("Other", schema => schema.Type("string"))
            .WithSchema("Bad", schema => schema.Type("string").AllOf(element => element.Ref("Other"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_Wrapper_When_Element_Carries_An_Applicator()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("string").AllOf(element => element.Raw("not", "{\"type\":\"number\"}"))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
    }

    [Test]
    public async Task Project_Should_Produce_Number_Literal_From_Single_Value_Enum()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Value",
            schema => schema.Type("number").Raw("enum", "[1]")));

        var result = await host.ProjectAsync(scenario);

        var literal = (LiteralNode)result.Schemas["Value"];
        await Assert.That(literal.Kind).IsEqualTo(LiteralKind.Number);
        await Assert.That(literal.Value).IsEqualTo("1");
        await Assert.That(literal.Dialect).IsEqualTo(LiteralDialect.SingleValueEnum);
    }

    [Test]
    public async Task Project_Should_Produce_Number_Literal_From_Single_Value_Integer_Enum()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Value",
            schema => schema.Type("integer").Raw("enum", "[3]")));

        var result = await host.ProjectAsync(scenario);

        var literal = (LiteralNode)result.Schemas["Value"];
        await Assert.That(literal.Kind).IsEqualTo(LiteralKind.Number);
        await Assert.That(literal.Value).IsEqualTo("3");
    }

    [Test]
    public async Task Project_Should_Refuse_Number_Enum_When_It_Has_Multiple_Values()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("number").Raw("enum", "[1, 2]")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("enum");
        await Assert.That(ex.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Refuse_Number_Enum_When_Its_Value_Is_Not_A_Number()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema.Type("number").Raw("enum", "[\"one\"]")));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("must contain a number");
    }

    [Test]
    public async Task Project_Should_Refuse_AllOf_When_Host_Carries_Other_Constraints()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema(
            "Bad",
            schema => schema
                .Type("object")
                .Property("value", property => property.Type("string"))
                .AllOf(element => element.Raw("pattern", "\"^a\""))));

        var ex = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(ex.Message).Contains("allOf");
    }
}
