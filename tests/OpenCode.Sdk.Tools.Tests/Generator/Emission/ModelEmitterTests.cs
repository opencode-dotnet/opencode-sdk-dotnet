using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ModelEmitterTests
{
    [Test]
    [Arguments(false, "JsonElement")]
    [Arguments(true, "FeedPayload")]
    public async Task Emit_Should_Use_JsonElement_For_An_Object_Or_Array_Union(bool typed, string expectedType)
    {
        var document = await BindingTestHost.IngestAsync(UnionScenario(typed ? ArrangeTypedObjectOrArray : ArrangeUnrestrictedObjectOrArray));
        var plan = new BindingTestHost().Bind(document, Selection("health.get"), Curation(Groups("health", RootGroup())));
        var payload = plan.Models.OfType<ObjectModelPlan>().Single(model => model.Name == "Feed").Properties.Single();
        var root = await CSharpSyntaxTree.ParseText(EmitterSnapshot.Create(ModelEmitter.Emit(plan))).GetRootAsync();
        var property = root.DescendantNodes().OfType<RecordDeclarationSyntax>().Single(record => record.Identifier.ValueText == "Feed")
            .Members.OfType<PropertyDeclarationSyntax>().Single(member => member.Identifier.ValueText == "Payload");

        await Assert.That(((NamedTypeReferencePlan)payload.Type).Name).IsEqualTo(expectedType);
        await Assert.That(property.Type.ToString()).IsEqualTo(expectedType);
        await Assert.That(plan.Models.OfType<StructuralUnionModelPlan>().Select(model => model.Name))
            .IsEquivalentTo(typed ? ["FeedPayload"] : [], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    private static void ArrangeTypedObjectOrArray(SpecDocumentBuilder spec) => spec
        .WithSchema("Item", schema => schema.Type("object")
            .Property("id", property => property.Type("string"), required: true))
        .WithSchema("Feed", schema => schema.Type("object")
            .Property("payload", property => property.AnyOf(
                branch => branch.Ref("Item"), branch => branch.Type("array").Items(item => item.Ref("Item"))), required: true));

    private static void ArrangeUnrestrictedObjectOrArray(SpecDocumentBuilder spec) => spec
        .WithSchema("Feed", schema => schema.Type("object")
            .Property("payload", property => property.AnyOf(
                branch => branch.Type("object"), branch => branch.Type("array")), required: true));

    [Test]
    public async Task Emit_Should_Preserve_Binding_Union_Membership_Order_In_Record_Base_Lists()
    {
        var document = await BindingTestHost.IngestAsync(UnionScenario(spec => spec
            .WithSchema("SharedError", schema => schema.Type("object")
                .Property("_tag", property => property.Type("string").Enum("shared"), required: true))
            .WithSchema("Other", schema => schema.Type("object")
                .Property("_tag", property => property.Type("string").Enum("other"), required: true))
            .WithSchema("AOuter", schema => schema.AnyOf(branch => branch.Ref("SharedError"), branch => branch.Ref("Other")))
            .WithSchema("ZOuter", schema => schema.OneOf(branch => branch.Ref("SharedError"), branch => branch.Ref("Other")))
            .WithSchema("Feed", schema => schema.Type("object")
                .Property("first", property => property.Ref("AOuter"), required: true)
                .Property("last", property => property.Ref("ZOuter"), required: true))));
        var plan = new BindingTestHost().Bind(document, Selection("health.get"),
            Curation(Groups("health", RootGroup()), schemaNames: [SchemaName("AOuter", "IZulu"), SchemaName("ZOuter", "IAlpha")]));
        var model = plan.Models.OfType<ObjectModelPlan>().Single(model => model.Name == "SharedError");
        var source = EmitterSnapshot.Create(ModelEmitter.Emit(plan));
        var root = await CSharpSyntaxTree.ParseText(source).GetRootAsync();
        var record = root.DescendantNodes().OfType<RecordDeclarationSyntax>()
            .Single(record => record.Identifier.ValueText == "SharedError");

        await Assert.That(record.BaseList!.Types.Select(type => type.Type.ToString()))
            .IsEquivalentTo(["IZulu", "IAlpha", "IOpenCodeError"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(model.ImplementedUnionNames)
            .IsEquivalentTo(["IZulu", "IAlpha", "IOpenCodeError"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Emit_Should_Choose_The_Exact_First_Word_Article()
    {
        var member = UnionHoistPlanData.Property("value", UnionHoistPlanData.Named("string"), isRequired: true);
        var plan = (EmitterPlanFixture.CreateModelSnapshot() with { Models = [UnionHoistPlanData.Record("hour", member)], Unions = [] }).WithSingleSlice();
        var source = EmitterSnapshot.Create(ModelEmitter.Emit(plan));
        await Assert.That(source).Contains("Represents an hour value.");
        await Assert.That(source).Contains(EmitterPlanFixture.ValueSummary);
    }

    [Test]
    public async Task Emit_Should_Produce_Immutable_Annotated_Models()
    {
        var sources = ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot());

        await Verify(EmitterSnapshot.Create(sources));
    }

    [Test]
    public async Task Emit_Should_Use_Shallow_Init_Only_Collection_References()
    {
        var source = EmitterSnapshot.Create(ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot()));

        await Assert.That(source).Contains("public IReadOnlyList<string>? Tags { get; init; }");
        await Assert.That(source).Contains("public IReadOnlyDictionary<string, Uri>? Links { get; init; }");
        await Assert.That(source).Contains("public required IReadOnlyList<string> RequiredTags { get; init; }");
        await Assert.That(source).DoesNotContain("OptionalCollectionInput");
        await Assert.That(source).DoesNotContain("new ReadOnlyDictionary");
        await Assert.That(source).DoesNotContain("new List");
    }

    [Test]
    public async Task Emit_Should_Write_Null_Only_For_Required_Nullable_Properties()
    {
        var source = EmitterSnapshot.Create(ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot()));

        await Assert.That(source).Contains("[JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]");
        await Assert.That(source).Contains("public required string? RequiredNullable { get; init; }");
        await Assert.That(source).DoesNotContain("WireNullRejecting");
    }

    [Test]
    public async Task Emit_Should_Emit_A_Prefix_Discriminator_As_A_Guarded_Required_String()
    {
        var sources = ModelEmitter.Emit(EmitterPlanFixture.Create());
        var arm = EmitterSnapshot.Content(sources, "Example/Models/RpcEvent.cs");

        await Assert.That(arm).Contains("[JsonPropertyName(\"type\")]");
        await Assert.That(arm).Contains("public required string Type");
        await Assert.That(arm).Contains("if (!value.StartsWith(\"rpc.\", StringComparison.Ordinal))");
        await Assert.That(arm).Contains("The 'type' marker must carry the 'rpc.' prefix.");
        await Assert.That(arm).Contains("field = value;");
        await Assert.That(arm).DoesNotContain("public string Type => \"");
    }

    /// <summary>
    /// An open model keeps its named members and adds one extension-data member typed the way
    /// System.Text.Json requires (<c>IDictionary</c>, not the read-only interface every other
    /// dictionary member uses) and shaped the way its source generator can fill it (get-only
    /// over a pre-built bag, never init-only), so the wire members the document leaves open
    /// survive a round trip.
    /// </summary>
    [Test]
    public async Task Emit_Should_Render_An_Extension_Data_Member_For_An_Open_Model()
    {
        var sources = ModelEmitter.Emit(EmitterPlanFixture.CreateModelSnapshot());
        var model = EmitterSnapshot.Content(sources, "Example/Models/OpenSettings.cs");

        await Assert.That(model).Contains("using OpenCode.Sdk.Internal;");
        await Assert.That(model).Contains("using System;");
        await Assert.That(model).Contains("using System.Collections.Generic;");
        await Assert.That(model).Contains("using System.Linq;");
        await Assert.That(model).Contains("using System.Text.Json;");
        await Assert.That(model).Contains("public double? Timeout { get; init; }");
        // The public view: read-only typed, init-only, invisible to the serializer, never null.
        await Assert.That(model).Contains("[JsonIgnore]");
        await Assert.That(model).Contains("public IReadOnlyDictionary<string, JsonElement> AdditionalProperties");
        await Assert.That(model).Contains("get => OpenMembers ?? EmptyOpenMembers.Instance;");
        await Assert.That(model).Contains("OpenMembers = value.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);");
        // The serializer's bag: settable, internal, admitted by JsonInclude, created lazily.
        await Assert.That(model).Contains("[JsonExtensionData]");
        await Assert.That(model).Contains("[JsonInclude]");
        await Assert.That(model).Contains("internal Dictionary<string, JsonElement>? OpenMembers { get; set; }");
        await Assert.That(model).DoesNotContain("new Dictionary");
        await Assert.That(model).DoesNotContain("public IDictionary");
        await Assert.That(model).DoesNotContain("[JsonPropertyName(\"additionalProperties\")]");
    }

    [Test]
    public async Task Emit_Should_Apply_Number_Handling_Only_To_Unwrapped_Special_Numbers()
    {
        var model = UnionHoistPlanData.Record("WidgetPatchRequest",
            UnionHoistPlanData.Property("required", UnionHoistPlanData.SpecialNumber(), isRequired: true),
            UnionHoistPlanData.Property("optional", UnionHoistPlanData.SpecialNumber(isNullable: true), isRequired: false),
            UnionHoistPlanData.Property("wrapped", UnionHoistPlanData.SpecialNumber(isNullable: true), isRequired: false, emitsOptionalWrapper: true));
        var plan = (EmitterPlanFixture.CreateModelSnapshot() with { Models = [model], Unions = [] }).WithSingleSlice();
        var root = await CSharpSyntaxTree.ParseText(EmitterSnapshot.Create(ModelEmitter.Emit(plan))).GetRootAsync();
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .ToDictionary(property => property.Identifier.ValueText, StringComparer.Ordinal);

        await Assert.That(properties["Required"].ToString()).Contains("[JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]");
        await Assert.That(properties["Optional"].ToString()).Contains("[JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]");
        await Assert.That(properties["Wrapped"].ToString()).DoesNotContain("JsonNumberHandling");
        await Assert.That(properties["Wrapped"].ToString()).Contains("[JsonConverter(typeof(OptionalOfSpecialNumberJsonConverter))]");
    }
}
