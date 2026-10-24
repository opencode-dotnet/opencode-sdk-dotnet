using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class UnionChainBuilderTests
{
    [Test]
    public async Task Bind_Should_Resolve_Links_And_Terminal()
    {
        var plan = await BindAsync();
        var chain = plan.Unions.Single(static union => union.Name == "IBeta").Chain;
        await Assert.That(chain.Select(static link => link.Name)).IsEquivalentTo(["IBeta", "IZulu"], CollectionOrdering.Matching);
        await Assert.That(chain.Select(static link => link.MarkerWireName)).IsEquivalentTo(["phase", "type"], CollectionOrdering.Matching);
        await Assert.That(chain.Select(static link => link.MarkerName)).IsEquivalentTo(["Phase", "Type"], CollectionOrdering.Matching);
        await Assert.That(chain.Select(static link => link.IsTerminal)).IsEquivalentTo([false, false], CollectionOrdering.Matching);
        var context = new StreamOperationScenario().Build();
        var wire = await context.FileSystem.File.ReadAllTextAsync(context.SpecPath);
        var stream = await BindingTestHost.IngestAsync(SpecScenario.FromRawJson(wire));
        var bound = new BindingTestHost().Bind(stream, Selection(StreamOperationScenario.OperationId), Curation(Groups("example", RootGroup())));
        var terminal = bound.Unions.Single(static union => union.Name == "IStreamFailureCause").Chain;
        await Assert.That(terminal.Select(static link => link.Name)).IsEquivalentTo(["IStreamFailureCause", "IOpenCodeStreamFailureCause"], CollectionOrdering.Matching);
        await Assert.That(terminal[1].MarkerWireName).IsEqualTo("_tag");
        await Assert.That(terminal[1].MarkerName).IsEqualTo("Tag");
        await Assert.That(terminal[1].MarkerKind).IsEqualTo(LiteralKind.String);
        await Assert.That(terminal[1].IsTerminal).IsTrue();
        await Assert.That(terminal[1].HoistedMembers).IsEmpty();
        await Assert.That(terminal[1].Variants).IsEmpty();
        await Assert.That(terminal[1].PrefixVariant).IsNull();
    }

    [Test]
    public async Task Bind_Should_Preserve_Membership_And_Marker_Order()
    {
        var plan = await BindAsync();
        var started = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Started");
        await Assert.That(started.ImplementedUnionNames).IsEquivalentTo(["IAlpha", "IBeta"], CollectionOrdering.Matching);
        await Assert.That(started.ChainMarkers).IsEquivalentTo(
            [new ChainMarkerPlan("phase", "Phase", null), new ChainMarkerPlan("type", "Type", null)], CollectionOrdering.Matching);
        var rpc = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Rpc");
        await Assert.That(rpc.ChainMarkers).IsEquivalentTo([new ChainMarkerPlan("type", "Type", "rpc.")], CollectionOrdering.Matching);
        await Assert.That(plan.Namespaces).IsEqualTo(new GeneratedNamespacePlan("OpenCode.Sdk", "OpenCode.Sdk.Models", "OpenCode.Sdk.Internal"));
        await Assert.That(plan.QueryEnumModels.Keys.Order(StringComparer.Ordinal)).IsEquivalentTo(["Example"], CollectionOrdering.Matching);
        await Assert.That(plan.QueryEnumModels["Example"].Select(static model => model.Name))
            .IsEquivalentTo(["AMode", "ZMode"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Preserve_Hoisted_Requiredness_And_Owners()
    {
        var plan = await BindAsync([Owner("IZulu"), Owner("IAlpha")]);
        var links = plan.Unions.Single(static union => union.Name == "IBeta").Chain;
        await Assert.That(links.SelectMany(static link => link.HoistedMembers).Select(static member => member.Name))
            .IsEquivalentTo(["Zeta", "Alpha"], CollectionOrdering.Matching);
        await Assert.That(links.SelectMany(static link => link.HoistedMembers).Select(static member => member.IsRequired))
            .IsEquivalentTo([true, false], CollectionOrdering.Matching);
        var started = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Started");
        await Assert.That(started.InheritedMemberOwners.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            .IsEquivalentTo([new KeyValuePair<string, string>("Zeta", "IAlpha.Zeta")], CollectionOrdering.Matching);
    }

    private static DescriptionCuration Owner(string type) => new()
    {
        Type = type,
        Member = "Zeta",
        Summary = "The shared identifier.",
        Reason = "Documents the shared wire member.",
        Sources = [new() { Location = "packages/example.ts:1", Excerpt = "An explicit declaration." }],
    };

    private static async Task<EmitPlan> BindAsync(IReadOnlyList<DescriptionCuration>? rows = null)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(Arrange));
        return new BindingTestHost().Bind(document, Selection("example.get"), Curation(Groups("example", RootGroup())) with { Descriptions = rows ?? [] });
    }

    private static void Arrange(SpecDocumentBuilder spec) => spec
        .WithSchema("Zulu", static schema => schema.AnyOf(static arm => arm.Ref("ZuluLeaf"), static arm => arm.Ref("Beta"), static arm => arm.Ref("Rpc")))
        .WithSchema("Beta", static schema => schema.AnyOf(static arm => arm.Ref("Started"), static arm => arm.Ref("Ended")))
        .WithSchema("Alpha", static schema => schema.AnyOf(static arm => arm.Ref("Started"), static arm => arm.Ref("Ended")))
        .WithSchema("Rpc", static schema => Leaf(schema, "rpc").Property("type", static marker => marker.Type("string").Pattern("^rpc\\.[\\s\\S]*?$"), required: true))
        .WithSchema("ZMode", static schema => schema.Type("string").Enum("fast", "slow"))
        .WithSchema("AMode", static schema => schema.Type("string").Enum("on", "off"))
        .WithSchema("ZuluLeaf", static schema => Leaf(schema, "leaf"))
        .WithSchema("Started", static schema => Leaf(schema, "phase").Property("phase", static marker => marker.Type("string").Enum("started"), required: true))
        .WithSchema("Ended", static schema => Leaf(schema, "phase").Property("phase", static marker => marker.Type("string").Enum("ended"), required: true))
        .WithSchema("Holder", static schema => schema.Type("object").Property("zulu", static property => property.Ref("Zulu"))
            .Property("alpha", static property => property.Ref("Alpha")))
        .WithOperation("example.get", configure: static operation => operation
            .Parameter("zmode", "query", static schema => schema.Ref("ZMode"))
            .Parameter("amode", "query", static schema => schema.Ref("AMode"))
            .Response(200, "application/json", static schema => schema.Ref("Holder")));

    private static SchemaBuilder Leaf(SchemaBuilder schema, string tag) => schema.Type("object")
        .Property("type", marker => marker.Type("string").Enum(tag), required: true)
        .Property("zeta", static property => property.Type("string"), required: true)
        .Property("alpha", static property => property.Type("string"));
}
