using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Projection;

public sealed class UnionNormalizerTests
{
    [Test]
    public async Task Project_Should_Classify_Marked_AnyOf_Union_Of_References()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("Created", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("created"), required: true))
            .WithSchema("Deleted", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("deleted"), required: true))
            .WithSchema("Event", schema => schema.AnyOf(branch => branch.Ref("Created"), branch => branch.Ref("Deleted"))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["Event"];
        await Assert.That(union.Keyword).IsEqualTo(UnionKeyword.AnyOf);
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Marked);
        await Assert.That(union.Branches).Count().IsEqualTo(2);
        await Assert.That(union.Children).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Project_Should_Preserve_OneOf_Keyword()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("Started", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("started"), required: true))
            .WithSchema("Stopped", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("stopped"), required: true))
            .WithSchema("DurableEvent", schema => schema.OneOf(branch => branch.Ref("Started"), branch => branch.Ref("Stopped"))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["DurableEvent"];
        await Assert.That(union.Keyword).IsEqualTo(UnionKeyword.OneOf);
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Marked);
    }

    [Test]
    public async Task Project_Should_Classify_Structural_Union_When_Branches_Carry_No_Markers()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Formatter", schema => schema.AnyOf(
            branch => branch.Type("boolean"),
            branch => branch.Type("object").AdditionalProperties(value => value.Type("string")))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["Formatter"];
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Structural);
    }

    [Test]
    public async Task Project_Should_Promote_Marked_Inline_Union_Branches_With_Marker_Keys()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Evt", schema => schema.AnyOf(
            branch => branch
                .Type("object")
                .Property("type", property => property.Type("string").Enum("created"), required: true),
            branch => branch
                .Type("object")
                .Property("type", property => property.Type("string").Enum("deleted"), required: true))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["Evt"];
        await Assert.That(((RefNode)union.Branches[0]).Target).IsEqualTo("Evt#/anyOf/type=created");
        await Assert.That(((RefNode)union.Branches[1]).Target).IsEqualTo("Evt#/anyOf/type=deleted");
        await Assert.That(result.Schemas["Evt#/anyOf/type=created"]).IsTypeOf<ObjectNode>();
        await Assert.That(result.Schemas["Evt#/anyOf/type=deleted"]).IsTypeOf<ObjectNode>();
    }

    [Test]
    public async Task Project_Should_Use_Ordinal_Key_For_Unmarked_Inline_Union_Branch()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Formatter", schema => schema.AnyOf(
            branch => branch.Type("object").Property("command", property => property.Type("string")),
            branch => branch.Type("boolean"))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["Formatter"];
        await Assert.That(((RefNode)union.Branches[0]).Target).IsEqualTo("Formatter#/anyOf/0");
        await Assert.That(result.Schemas["Formatter#/anyOf/0"]).IsTypeOf<ObjectNode>();
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Structural);
    }

    [Test]
    public async Task Project_Should_Deduplicate_Repeated_Reference_Branches()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("A", schema => schema.Type("string"))
            .WithSchema("B", schema => schema.Type("number"))
            .WithSchema("Choice", schema => schema.AnyOf(branch => branch.Ref("A"), branch => branch.Ref("B"), branch => branch.Ref("B"))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["Choice"];
        await Assert.That(union.Branches).Count().IsEqualTo(2);
        await Assert.That(((RefNode)union.Branches[0]).Target).IsEqualTo("A");
        await Assert.That(((RefNode)union.Branches[1]).Target).IsEqualTo("B");
    }

    [Test]
    public async Task Project_Should_Collapse_Deduplicated_Single_Reference_Union()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("A", schema => schema.Type("string"))
            .WithSchema("Choice", schema => schema.AnyOf(branch => branch.Ref("A"), branch => branch.Ref("A"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Choice"]).IsTypeOf<RefNode>();
        await Assert.That(((RefNode)result.Schemas["Choice"]).Target).IsEqualTo("A");
    }

    [Test]
    public async Task Project_Should_Extract_Null_Branch_Into_Nullable_Node()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("MaybeName", schema => schema.AnyOf(
            branch => branch.Type("string"),
            branch => branch.Type("null"))));

        var result = await host.ProjectAsync(scenario);

        var nullable = (NullableNode)result.Schemas["MaybeName"];
        await Assert.That(nullable.Inner).IsTypeOf<PrimitiveNode>();
        await Assert.That(nullable.Children.Single()).IsSameReferenceAs(nullable.Inner);
    }

    [Test]
    public async Task Project_Should_Extract_Null_After_Deduplicating_References()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("A", schema => schema.Type("string"))
            .WithSchema("MaybeA", schema => schema.AnyOf(
                branch => branch.Ref("A"),
                branch => branch.Ref("A"),
                branch => branch.Type("null"))));

        var result = await host.ProjectAsync(scenario);

        var nullable = (NullableNode)result.Schemas["MaybeA"];
        await Assert.That(nullable.Inner).IsTypeOf<RefNode>();
        await Assert.That(((RefNode)nullable.Inner).Target).IsEqualTo("A");
    }

    [Test]
    public async Task Project_Should_Normalize_Special_Number_Before_Projecting_Branches()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithRawSchema("Workspace.timeUsed", "special-number.json"));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Workspace.timeUsed"]).IsTypeOf<SpecialNumberNode>();
        await Assert.That(result.Schemas).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Project_Should_Normalize_Special_Number_With_Const_Literal_Branch()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Special", schema => schema.AnyOf(
            branch => branch.Type("number"),
            branch => branch.Type("string").Const("NaN"))));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(result.Schemas["Special"]).IsTypeOf<SpecialNumberNode>();
    }

    [Test]
    public async Task Project_Should_Fold_A_Flat_Special_Number_Into_One_Branch_Of_A_Wider_Union()
    {
        var result = await ProjectUnionAsync("Value",
            branch => branch.Type("string"),
            branch => branch.Type("number"),
            SpecialSpellings,
            branch => branch.Type("boolean"));

        await Assert.That(((UnionNode)result.Schemas["Value"]).Branches.Select(BranchKind))
            .IsEquivalentTo(["String", nameof(SpecialNumberNode), "Boolean"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_Should_Fold_A_Flat_Nullable_Special_Number()
    {
        var result = await ProjectUnionAsync("Dcid",
            branch => branch.Type("number"),
            SpecialSpellings,
            branch => branch.Type("null"));

        await Assert.That(((NullableNode)result.Schemas["Dcid"]).Inner).IsTypeOf<SpecialNumberNode>();
    }

    [Test]
    public async Task Project_Should_Fold_Several_Flat_Special_Spellings_Beside_Another_Branch()
    {
        var result = await ProjectUnionAsync("NearMiss",
            branch => branch.Type("number"),
            branch => branch.Type("string").Enum("NaN"),
            branch => branch.Type("string").Enum("Infinity"),
            branch => branch.Type("string").Enum("-Infinity"),
            SpecialSpellings,
            branch => branch.Type("boolean"));

        var union = (UnionNode)result.Schemas["NearMiss"];
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Structural);
        await Assert.That(union.Branches.Select(BranchKind)).IsEquivalentTo([nameof(SpecialNumberNode), "Boolean"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_Should_Keep_A_Number_Apart_From_Its_Special_Spellings_As_Ordinary_Union()
    {
        var result = await ProjectUnionAsync("Apart",
            branch => branch.Type("number"),
            branch => branch.Type("boolean"),
            SpecialSpellings);

        await Assert.That(((UnionNode)result.Schemas["Apart"]).Branches.Select(BranchKind))
            .IsEquivalentTo(["Number", "Boolean", nameof(RefNode)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_Should_Keep_A_Number_Beside_A_Non_Special_String_Enum_As_Ordinary_Union()
    {
        var result = await ProjectUnionAsync("NearMiss",
            branch => branch.Type("number"),
            branch => branch.Type("string").Enum("Infinity", "Other"),
            branch => branch.Type("boolean"));

        await Assert.That(((UnionNode)result.Schemas["NearMiss"]).Branches.Select(BranchKind))
            .IsEquivalentTo(["Number", nameof(RefNode), "Boolean"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_Should_Keep_Two_Numbers_Beside_Special_Spellings_As_Ordinary_Union()
    {
        var result = await ProjectUnionAsync("Twice",
            branch => branch.Type("number"),
            branch => branch.Type("number").Format("double"),
            SpecialSpellings);

        await Assert.That(((UnionNode)result.Schemas["Twice"]).Branches.Select(BranchKind))
            .IsEquivalentTo(["Number", "Number", nameof(RefNode)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_Should_Refuse_A_Folded_Member_The_Wall_Refuses()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.AnyOf(
            branch => branch.Type("string"),
            branch => branch.Type("number").Raw("frobnicate", "1"),
            SpecialSpellings)));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Errors).IsEquivalentTo(
            [new IngestionError("Value/anyOf/1", "unrecognized schema keyword 'frobnicate' is not supported")], CollectionOrdering.Matching);
    }

    /// <summary>A string enum beside a free string is an open enum: the union is one enum of the enum's values.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Project_Should_Fold_An_Open_Enum_Into_One_Enum(bool nullable)
    {
        Action<SchemaBuilder>[] branches = [static branch => branch.Type("string").Enum("chat", "image"), static branch => branch.Type("string").Raw("pattern", "\"^rpc\\\\.\"")];
        var result = await ProjectUnionAsync("Kind", nullable ? [.. branches, static branch => branch.Type("null")] : branches);

        var node = result.Schemas["Kind"] is NullableNode { Inner: var inner } ? inner : result.Schemas["Kind"];
        await Assert.That(result.Schemas["Kind"] is NullableNode).IsEqualTo(nullable);
        await Assert.That(((EnumNode)node).Values).IsEquivalentTo(["chat", "image"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_Should_Fold_An_Open_Enum_Beside_A_List_Of_It()
    {
        var result = await ProjectUnionAsync("Kinds",
            static branch => branch.Type("string").Enum("chat", "image"),
            static branch => branch.Type("string"),
            static branch => branch.Type("array").Items(static item => item.AnyOf(
                static arm => arm.Type("string").Enum("chat", "image"),
                static arm => arm.Type("string"))),
            static branch => branch.Type("null"));

        await Assert.That(((UnionNode)((NullableNode)result.Schemas["Kinds"]).Inner).Branches.Select(BranchKind))
            .IsEquivalentTo([nameof(RefNode), nameof(ArrayNode)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Project_Should_Keep_An_Enum_Apart_From_A_Free_String_As_Ordinary_Union()
    {
        var result = await ProjectUnionAsync("Apart",
            static branch => branch.Type("string").Enum("chat", "image"),
            static branch => branch.Type("boolean"),
            static branch => branch.Type("string"));

        await Assert.That(((UnionNode)result.Schemas["Apart"]).Branches.Select(BranchKind))
            .IsEquivalentTo([nameof(RefNode), "Boolean", "String"], CollectionOrdering.Matching);
    }

    /// <summary>The fold never projects its members, so a member the wall refuses still refuses, whole or beside a list.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Project_Should_Refuse_An_Open_Enum_Member_The_Wall_Refuses(bool besideList)
    {
        var host = new SchemaProjectionTestHost();
        Action<SchemaBuilder>[] branches =
        [
            static branch => branch.Type("string").Enum("chat", "image"),
            static branch => branch.Type("string").Raw("frobnicate", "1"),
        ];
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Kind", schema => schema.AnyOf(
            besideList ? [.. branches, static branch => branch.Type("array").Items(static item => item.Type("string"))] : branches)));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Errors).IsEquivalentTo(
            [new IngestionError("Kind/anyOf/1", "unrecognized schema keyword 'frobnicate' is not supported")], CollectionOrdering.Matching);
    }

    /// <summary>An enum beside no free string, a non-string, or a formatted string is no open enum.</summary>
    [Test]
    [Arguments("boolean", "Boolean")]
    [Arguments("date-time", "String")]
    [Arguments("enum", nameof(RefNode))]
    public async Task Project_Should_Keep_An_Enum_Beside_No_Free_String_As_Ordinary_Union(string other, string otherKind)
    {
        var result = await ProjectUnionAsync("Mixed",
            static branch => branch.Type("string").Enum("chat", "image"),
            branch => OtherBranch(branch, other));

        await Assert.That(((UnionNode)result.Schemas["Mixed"]).Branches.Select(BranchKind))
            .IsEquivalentTo([nameof(RefNode), otherKind], CollectionOrdering.Matching);
    }

    /// <summary>A branch keyed by its own required key refuses when that key is another branch's ordinal key.</summary>
    [Test]
    public async Task Project_Should_Refuse_An_Own_Key_That_Collides_With_An_Ordinal_Key()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Value", schema => schema.AnyOf(
            branch => branch.Type("object").Property("label", property => property.Type("string")),
            branch => branch.Type("object").Property("0", property => property.Type("string"), required: true))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("schema graph key collision for 'Value#/anyOf/0'");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Special_Number_Branch_Has_Competing_Constraint()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.AnyOf(
            branch => branch.Type("number").Property("ignored", property => property.Type("string")),
            branch => branch.Type("string").Enum("NaN"))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("properties");
        await Assert.That(exception.Message).Contains("Bad/anyOf/0");
    }

    [Test]
    public async Task Project_Should_Keep_Boolean_And_String_Enum_As_Union()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("AutoUpdate", schema => schema.AnyOf(
            branch => branch.Type("boolean"),
            branch => branch.Type("string").Enum("notify", "never"))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["AutoUpdate"];
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Structural);
        await Assert.That(union.Branches).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Project_Should_Refuse_When_AnyOf_And_OneOf_Are_Combined()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema
            .AnyOf(branch => branch.Type("string"))
            .OneOf(branch => branch.Type("number"))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("anyOf");
        await Assert.That(exception.Message).Contains("oneOf");
        await Assert.That(exception.Message).Contains("cannot be combined");
    }

    [Test]
    public async Task Project_Should_Refuse_When_Null_Extraction_Leaves_No_Branch()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("Bad", schema => schema.AnyOf(branch => branch.Type("null"))));

        var exception = await host.ProjectExpectingRefusalAsync(scenario);

        await Assert.That(exception.Message).Contains("zero");
        await Assert.That(exception.Message).Contains("Bad");
    }

    [Test]
    public async Task Project_Should_Classify_A_Union_With_One_Prefix_Tagged_Branch_As_Marked()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("Created", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("created"), required: true))
            .WithSchema("RpcEvent", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Pattern("^rpc\\.[\\s\\S]*?$"), required: true))
            .WithSchema("Event", schema => schema.AnyOf(branch => branch.Ref("Created"), branch => branch.Ref("RpcEvent"))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["Event"];
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Marked);
        await Assert.That(union.Branches).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Project_Should_Keep_A_Union_Structural_When_A_Branch_Has_Neither_Literal_Nor_Prefix()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec
            .WithSchema("Created", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Enum("created"), required: true))
            .WithSchema("Loose", schema => schema
                .Type("object")
                .Property("type", property => property.Type("string").Pattern("^evt_"), required: true))
            .WithSchema("Event", schema => schema.AnyOf(branch => branch.Ref("Created"), branch => branch.Ref("Loose"))));

        var result = await host.ProjectAsync(scenario);

        var union = (UnionNode)result.Schemas["Event"];
        await Assert.That(union.Classification).IsEqualTo(UnionClassification.Structural);
    }

    private static Task<SchemaProjectionResult> ProjectUnionAsync(string name, params Action<SchemaBuilder>[] branches) =>
        new SchemaProjectionTestHost().ProjectAsync(SpecScenario.Define(spec => spec.WithSchema(name, schema => schema.AnyOf(branches))));

    private static void OtherBranch(SchemaBuilder branch, string other) => _ = other switch
    {
        "boolean" => branch.Type("boolean"),
        "date-time" => branch.Type("string").Format("date-time"),
        _ => branch.Type("string").Enum("audio", "video"),
    };

    private static void SpecialSpellings(SchemaBuilder branch) => branch.Type("string").Enum("Infinity", "-Infinity", "NaN");

    private static string BranchKind(SchemaNode branch) => branch is PrimitiveNode primitive ? primitive.Kind.ToString() : branch.GetType().Name;
}
