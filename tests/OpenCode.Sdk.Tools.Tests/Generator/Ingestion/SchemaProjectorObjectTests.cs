using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

public sealed class SchemaProjectorObjectTests
{
    [Test]
    public async Task Project_Should_Keep_Property_Schema_And_Dictionary_Value_For_Hybrid_Objects()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("ProviderOptions", schema => schema
            .Type("object")
            .Property("timeout", property => property.Type("number"))
            .AdditionalProperties(value => value.Type("string"))));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["ProviderOptions"];
        await Assert.That(node.AdditionalProperties).IsEqualTo(AdditionalPropertiesKind.Schema);
        await Assert.That(node.AdditionalPropertiesSchema).IsTypeOf<PrimitiveNode>();
        await Assert.That(node.Properties.Single(property => property.Name == "timeout").IsRequired).IsFalse();
        await Assert.That(node.Children).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Project_Should_Record_Required_Properties()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Request", schema => schema
            .Type("object")
            .Property("requiredValue", property => property.Type("string"), required: true)
            .Property("optionalValue", property => property.Type("string"))
            .AdditionalPropertiesFalse()));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["Request"];
        await Assert.That(node.Properties[0].IsRequired).IsTrue();
        await Assert.That(node.Properties[1].IsRequired).IsFalse();
        await Assert.That(node.AdditionalProperties).IsEqualTo(AdditionalPropertiesKind.Forbidden);
        await Assert.That(node.LiteralMarkers).IsEmpty();
        await Assert.That(node.ErrorStyle).IsEqualTo(ErrorStyle.None);
    }

    [Test]
    public async Task Project_Should_Preserve_Property_Document_Order()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Ordered", schema => schema
            .Type("object")
            .Property("third", property => property.Type("string"))
            .Property("first", property => property.Type("string"))
            .Property("second", property => property.Type("string"))));

        var result = await host.ProjectAsync(scenario);

        var properties = ((ObjectNode)result.Schemas["Ordered"]).Properties;
        await Assert.That(properties).Count().IsEqualTo(3);
        await Assert.That(properties[0].Name).IsEqualTo("third");
        await Assert.That(properties[1].Name).IsEqualTo("first");
        await Assert.That(properties[2].Name).IsEqualTo("second");
    }

    [Test]
    public async Task Project_Should_Promote_Inline_Object_Property()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Envelope", schema => schema
            .Type("object")
            .Property("payload", property => property
                .Type("object")
                .Property("value", value => value.Type("string")))));

        var result = await host.ProjectAsync(scenario);

        var property = ((ObjectNode)result.Schemas["Envelope"]).Properties.Single();
        await Assert.That(property.Schema).IsTypeOf<RefNode>();
        await Assert.That(((RefNode)property.Schema).Target).IsEqualTo("Envelope#/properties/payload");
        await Assert.That(result.Schemas["Envelope#/properties/payload"]).IsTypeOf<ObjectNode>();
    }

    [Test]
    public async Task Project_Should_Treat_Keyword_Named_Properties_As_Wire_Data()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("GlobalEvent", schema => schema
            .Type("object")
            .Property("type", property => property.Type("string"))
            .Property("properties", property => property.Type("string"))
            .Property("required", property => property.Type("string"))));

        var result = await host.ProjectAsync(scenario);

        var properties = ((ObjectNode)result.Schemas["GlobalEvent"]).Properties;
        await Assert.That(properties).Count().IsEqualTo(3);
        await Assert.That(properties[0].Name).IsEqualTo("type");
        await Assert.That(properties[1].Name).IsEqualTo("properties");
        await Assert.That(properties[2].Name).IsEqualTo("required");
    }

    [Test]
    public async Task Project_Should_Produce_Dictionary_From_AdditionalProperties_Schema()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Labels", schema => schema
            .Type("object")
            .AdditionalProperties(value => value.Type("string"))));

        var result = await host.ProjectAsync(scenario);

        var node = (DictionaryNode)result.Schemas["Labels"];
        await Assert.That(node.Value).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)node.Value).Kind).IsEqualTo(PrimitiveKind.String);
    }

    [Test]
    public async Task Project_Should_Produce_Dictionary_From_Single_PatternProperties_Entry()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Sessions", schema => schema
            .Type("object")
            .PatternProperties("^.*$", value => value.Type("integer"))));

        var result = await host.ProjectAsync(scenario);

        var node = (DictionaryNode)result.Schemas["Sessions"];
        await Assert.That(node.Value).IsTypeOf<PrimitiveNode>();
        await Assert.That(((PrimitiveNode)node.Value).Kind).IsEqualTo(PrimitiveKind.Integer);
    }

    [Test]
    public async Task Project_Should_Refuse_When_PatternProperties_Has_Multiple_Entries()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .PatternProperties("^a", value => value.Type("string"))
            .PatternProperties("^b", value => value.Type("string"))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("patternProperties");
        await Assert.That(exception.Message).Contains("exactly one");
    }

    [Test]
    public async Task Project_Should_Refuse_When_PatternProperties_Is_Combined_With_Properties()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .Property("value", property => property.Type("string"))
            .PatternProperties("^.*$", value => value.Type("string"))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("patternProperties");
        await Assert.That(exception.Message).Contains("properties");
    }

    [Test]
    public async Task Project_Should_Produce_Dictionary_From_PatternProperties_Closed_By_AdditionalProperties_False()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Counts", schema => schema
            .Type("object")
            .PatternProperties("^ses", value => value.Type("integer"))
            .AdditionalPropertiesFalse()));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Counts"]).IsTypeOf<DictionaryNode>();
    }

    [Test]
    public async Task Project_Should_Refuse_When_PatternProperties_Is_Combined_With_AdditionalProperties_Schema()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .PatternProperties("^.*$", value => value.Type("string"))
            .AdditionalProperties(value => value.Type("string"))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("patternProperties");
        await Assert.That(exception.Message).Contains("additionalProperties");
    }

    [Test]
    public async Task Project_Should_Produce_FreeForm_Object_For_Bare_Object_Schema()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Metadata", schema => schema.Type("object")));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Metadata"]).IsTypeOf<FreeFormObjectNode>();
    }

    [Test]
    public async Task Project_Should_Produce_Object_For_Empty_Properties_Object()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Empty", schema => schema.Type("object").EmptyProperties()));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["Empty"];
        await Assert.That(node.Properties).IsEmpty();
        await Assert.That(node.AdditionalProperties).IsEqualTo(AdditionalPropertiesKind.Open);
    }

    [Test]
    public async Task Project_Should_Report_Required_Location_When_Name_Has_No_Property()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .EmptyProperties()
            .Required("missing")));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Errors).HasSingleItem();
        await Assert.That(exception.Errors.Single().Location).IsEqualTo("Bad/required");
        await Assert.That(exception.Errors.Single().Problem).Contains("missing");
    }

    [Test]
    public async Task Project_Should_Treat_Explicit_True_AdditionalProperties_As_Open()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Open", schema => schema
            .Type("object")
            .Property("value", property => property.Type("string"))
            .AdditionalPropertiesTrue()));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["Open"];
        await Assert.That(node.AdditionalProperties).IsEqualTo(AdditionalPropertiesKind.Open);
        await Assert.That(node.AdditionalPropertiesSchema).IsNull();
    }

    [Test]
    public async Task Project_Should_Record_A_Required_Template_Literal_Prefix_Marker()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("RpcEvent", schema => schema
            .Type("object")
            .Property("type", property => property.Type("string").Pattern("^rpc\\.[\\s\\S]*?$"), required: true)
            .Property("data", property => property.Type("object"), required: true)));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["RpcEvent"];
        await Assert.That(node.LiteralMarkers).IsEmpty();
        await Assert.That(node.PrefixMarkers).Count().IsEqualTo(1);
        await Assert.That(node.PrefixMarkers[0].PropertyName).IsEqualTo("type");
        await Assert.That(node.PrefixMarkers[0].Prefix).IsEqualTo("rpc.");
        await Assert.That(node.Properties.Single(static property => property.Name == "type").Schema).IsTypeOf<PrimitiveNode>();
    }

    [Test]
    public async Task Project_Should_Ignore_Optional_Formatted_Or_Ordinary_Patterns()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Plain", schema => schema
            .Type("object")
            .Property("id", property => property.Type("string").Pattern("^evt_"), required: true)
            .Property("optional", property => property.Type("string").Pattern("^rpc\\.[\\s\\S]*?$"))
            .Property("formatted", property => property.Type("string").Format("date-time").Pattern("^rpc\\.[\\s\\S]*?$"), required: true)));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["Plain"];
        await Assert.That(node.PrefixMarkers).IsEmpty();
    }

    /// <summary>
    /// Effect's <c>StructWithRest(Struct({…}), [Record(String, Any)])</c> lands as the struct's
    /// <c>properties</c> beside a single-element <c>allOf</c> whose only content is the rest's
    /// <c>additionalProperties</c>; the wrapper reads as the host's own additional properties.
    /// </summary>
    [Test]
    public async Task Project_Should_Fold_A_Rest_Wrapper_Into_The_Additional_Properties()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("ProviderSettings", schema => schema
            .Type("object")
            .Property("timeout", property => property.Type("number"))
            .AllOf(rest => rest.Type("object").AdditionalProperties(value => value.Unrestricted()))));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["ProviderSettings"];
        await Assert.That(node.AdditionalProperties).IsEqualTo(AdditionalPropertiesKind.Schema);
        await Assert.That(node.AdditionalPropertiesSchema).IsTypeOf<UnrestrictedNode>();
        await Assert.That(node.Properties.Single(static property => property.Name == "timeout").IsRequired).IsFalse();
        await Assert.That(node.Children).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Project_Should_Fold_A_Rest_Wrapper_Whose_Values_Admit_Null()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("ConfigProviderSettings", schema => schema
            .Type("object")
            .Property("timeout", property => property.Type("number"))
            .AllOf(rest => rest.Type("object").AdditionalProperties(value => value.AnyOf(
                any => any.Unrestricted(),
                nothing => nothing.Type("null"))))));

        var result = await host.ProjectAsync(scenario);

        var node = (ObjectNode)result.Schemas["ConfigProviderSettings"];
        await Assert.That(node.AdditionalProperties).IsEqualTo(AdditionalPropertiesKind.Schema);
        await Assert.That(node.AdditionalPropertiesSchema).IsTypeOf<NullableNode>();
        await Assert.That(((NullableNode)node.AdditionalPropertiesSchema!).Inner).IsTypeOf<UnrestrictedNode>();
    }

    [Test]
    public async Task Project_Should_Refuse_A_Rest_Wrapper_With_More_Than_One_Element()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .Property("timeout", property => property.Type("number"))
            .AllOf(
                rest => rest.Type("object").AdditionalProperties(value => value.Unrestricted()),
                more => more.Type("object").AdditionalProperties(value => value.Unrestricted()))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("allOf");
        await Assert.That(exception.Message).Contains("supported core shape");
    }

    [Test]
    public async Task Project_Should_Refuse_A_Rest_Wrapper_That_Declares_Properties()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .Property("timeout", property => property.Type("number"))
            .AllOf(rest => rest
                .Type("object")
                .Property("extra", property => property.Type("string"))
                .AdditionalProperties(value => value.Unrestricted()))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("allOf");
        await Assert.That(exception.Message).Contains("supported core shape");
    }

    [Test]
    public async Task Project_Should_Refuse_A_Rest_Wrapper_That_Is_A_Reference()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("Rest", schema => schema.Type("object").AdditionalProperties(value => value.Unrestricted()))
            .WithSchema("Bad", schema => schema
                .Type("object")
                .Property("timeout", property => property.Type("number"))
                .AllOf(rest => rest.Ref("Rest"))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("allOf");
        await Assert.That(exception.Message).Contains("supported core shape");
    }

    [Test]
    public async Task Project_Should_Refuse_A_Rest_Wrapper_Beside_The_Hosts_Own_Additional_Properties()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .Property("timeout", property => property.Type("number"))
            .AdditionalProperties(value => value.Type("string"))
            .AllOf(rest => rest.Type("object").AdditionalProperties(value => value.Unrestricted()))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("allOf");
        await Assert.That(exception.Message).Contains("supported core shape");
    }

    [Test]
    public async Task Project_Should_Refuse_A_Rest_Wrapper_On_A_Host_Without_Properties()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .Type("object")
            .AllOf(rest => rest.Type("object").AdditionalProperties(value => value.Unrestricted()))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("allOf");
        await Assert.That(exception.Message).Contains("supported core shape");
    }
}
