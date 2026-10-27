using System.Text.Json.Nodes;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// The stream-cause claim renames the cause union and its tagged variants everywhere, so it may
/// take only a union that cause schemas alone reference.
/// </summary>
public sealed class EffectStreamRootNameResolverTests
{
    private const string Failure = "Widget.Failure";
    private const string Watch = "widget.watch";
    private const string Log = "widget.log";
    private const string CausePointer = "#/responses/200/content/text~1event-stream/x-effect-stream/causeSchema/items";

    [Test]
    [Arguments("")]
    [Arguments(".Die")]
    public async Task Resolve_Should_Refuse_A_Cause_Union_Another_Reachable_Schema_References(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);

        var exception = await BindRefusalsAsync(ComponentCauseScenario(suffix), Curation(Groups("widget", RootGroup())), Watch, "widget.get");

        await Assert.That(exception.Errors).IsEquivalentTo([SharedCauseUnion("schema 'Widget.Info'", suffix)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Claim_An_Inline_Cause_Union_Shared_Only_By_Cause_Schemas()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec =>
        {
            _ = Frame(spec)
                .WithOperation(Watch, path: "/api/widget/watch", configure: static operation => operation
                    .SseResponseFromFixture(static schema => schema.Ref("Widget.Frame"), "effect-stream.json"))
                .WithOperation(Log, path: "/api/widget/log", configure: static operation => operation
                    .SseResponseFromFixture(static schema => schema.Ref("Widget.Frame"), "effect-stream.json"));
        }));
        var curation = Curation(Groups("widget", RootGroup()), schemaAliases: [Alias("op:" + Watch + CausePointer, "op:" + Log + CausePointer)]);

        var plan = new BindingTestHost().Bind(document, Selection(Watch, Log), curation);

        await Assert.That(plan.Clients.SelectMany(static client => client.Operations).Select(static operation => operation.Stream!.CauseTypeName))
            .IsEquivalentTo(["IStreamFailureCause[]", "IStreamFailureCause[]"], CollectionOrdering.Matching);
        await Assert.That(plan.Unions.Select(static union => union.Name)).Contains("IStreamFailureCause");
        await Assert.That(plan.Models.Select(static model => model.Name)).Contains("StreamFailureCauseDie");
    }

    [Test]
    public async Task Resolve_Should_Claim_A_Recursive_Cause_Union()
    {
        var document = await BindingTestHost.IngestAsync(ComponentCauseScenario("", recursive: true));

        var plan = new BindingTestHost().Bind(document, Selection(Watch), Curation(Groups("widget", RootGroup())));

        await Assert.That(plan.Clients.SelectMany(static client => client.Operations).Single().Stream!.CauseTypeName).IsEqualTo("IStreamFailureCause[]");
        await Assert.That(plan.Unions.Select(static union => union.Name)).Contains("IStreamFailureCause");
        await Assert.That(plan.Models.Select(static model => model.Name)).Contains("StreamFailureCauseInterrupt");
        var recursive = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "StreamFailureCauseDie");
        await Assert.That(((NamedTypeReferencePlan)recursive.Properties.Single(static property => property.WireName == "defect").Type).Name).IsEqualTo("IStreamFailureCause");
    }

    /// <summary>A component cause union or variant that a model property also references.</summary>
    private static SpecScenario ComponentCauseScenario(string suffix, bool recursive = false) => SpecScenario.Define(spec =>
    {
        _ = Frame(spec)
            .WithSchema(Failure, static schema => schema.AnyOf(static branch => branch.Ref(Failure + ".Die"), static branch => branch.Ref(Failure + ".Interrupt")))
            .WithSchema(Failure + ".Die", schema => Tagged(schema, "Die")
                .Property("defect", property => _ = recursive ? property.Ref(Failure) : property.Type("string"), required: true))
            .WithSchema(Failure + ".Interrupt", static schema => Tagged(schema, "Interrupt").Property("fiberId", static property => property.Type("number"), required: true))
            .WithSchema("Widget.Info", schema => schema
                .Type("object")
                .Property("failure", property => property.Ref(Failure + suffix), required: true))
            .WithOperation("widget.get", path: "/api/widget", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Widget.Info")))
            .WithOperation(Watch, path: "/api/widget/watch", configure: static operation => operation
                .SseResponse(static schema => schema.Ref("Widget.Frame"), ComponentCauseExtension()));
    });

    private static SpecDocumentBuilder Frame(SpecDocumentBuilder spec) => spec
        .WithSchema("Widget.Event", static schema => schema
            .Type("object")
            .Property("id", static property => property.Type("string"), required: true))
        .WithSchema("Widget.Frame", static schema => schema
            .Type("object")
            .Property("id", static property => property.AnyOf(static arm => arm.Type("string"), static arm => arm.Type("null")))
            .Property("event", static property => property.Type("string"), required: true)
            .Property("data", static property => property.Type("string").ContentSchema("application/json", static inner => inner.Ref("Widget.Event")), required: true));

    /// <summary>
    /// The pinned extension fixture, an object that declares a cause array, with its inline cause
    /// union replaced by a reference to the component union.
    /// </summary>
    private static string ComponentCauseExtension()
    {
        var extension = JsonNode.Parse(new FixtureLoader().Load("effect-stream.json"))!.AsObject();
        extension["causeSchema"]!["items"] = new SchemaBuilder().Ref(Failure).Build();
        return extension.ToJsonString();
    }

    private static SchemaBuilder Tagged(SchemaBuilder schema, string tag) => schema
        .Type("object")
        .Property("_tag", property => property.Type("string").Enum(tag), required: true);

    private static BindingError SharedCauseUnion(string referrer, string suffix) =>
        new(BindingErrorCategory.Naming, Failure + suffix,
            $"stream-cause {(suffix.Length == 0 ? "union" : "variant")} claim cannot rename a key also referenced by {referrer}");
}
