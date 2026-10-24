using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class DescriptionTargetCatalogTests
{
    [Test]
    [Arguments("type")]
    [Arguments("phase")]
    public async Task Create_Should_Distinguish_Declared_And_Inherited_Markers(string marker)
    {
        var document = await IngestAsync(spec => spec
            .WithSchema("Zulu", static schema => schema.AnyOf(static arm => arm.Ref("ZuluLeaf"), static arm => arm.Ref("Beta")))
            .WithSchema("Beta", static schema => schema.AnyOf(static arm => arm.Ref("Started"), static arm => arm.Ref("Ended")))
            .WithSchema("ZuluLeaf", static schema => Leaf(schema, "leaf"))
            .WithSchema("Started", schema => Leaf(schema, marker == "type" ? "started" : "phase")
                .Property(marker, static property => property.Type("string").Enum("started"), required: true))
            .WithSchema("Ended", schema => Leaf(schema, marker == "type" ? "ended" : "phase")
                .Property(marker, static property => property.Type("string").Enum("ended"), required: true))
            .WithSchema("Feed", static schema => schema.Type("object").Property("zulu", static property => property.Ref("Zulu"))));
        var plan = new BindingTestHost().Bind(document, Selection("example.get"), Curation(Groups("example", RootGroup())));
        var targets = DescriptionTargetCatalog.Create(plan);
        await Assert.That(targets["IZulu"].Order(StringComparer.Ordinal)).IsEquivalentTo(["Type"], CollectionOrdering.Matching);
        await Assert.That(targets["IBeta"].Order(StringComparer.Ordinal))
            .IsEquivalentTo(marker == "type" ? (string[])[] : ["Phase"], CollectionOrdering.Matching);
        await Assert.That(targets["UnknownZulu"].Order(StringComparer.Ordinal)).IsEquivalentTo(["Payload", "Type"], CollectionOrdering.Matching);
        await Assert.That(targets["UnknownBeta"].Order(StringComparer.Ordinal))
            .IsEquivalentTo(marker == "type" ? (string[])["Payload", "Type"] : ["Payload", "Phase", "Type"], CollectionOrdering.Matching);
        await Assert.That(targets["Started"].Order(StringComparer.Ordinal))
            .IsEquivalentTo(marker == "type" ? (string[])["Type"] : ["Phase", "Type"], CollectionOrdering.Matching);
        await Assert.That(plan.Unions.Single(static union => union.Name == "IBeta").Chain.Select(static link => link.Name))
            .IsEquivalentTo(["IBeta", "IZulu"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_Should_Name_Hoisted_And_Unknown_Members()
    {
        var curation = Curation(Groups("example", RootGroup()),
            hoistedMemberNames: [HoistedMemberName("IExampleEvent", "durable", "IExampleDurable")]) with
        {
            Descriptions = [new DescriptionCuration
            {
                Type = "IExampleEvent", Member = "Note", Summary = "The optional note.", Reason = "Documents the optional wire member.",
                Sources = [new() { Location = "packages/example.ts:1", Excerpt = "An explicit declaration." }],
            }],
        };
        var document = await IngestAsync(static spec => spec
            .WithSchema("ExampleEvent", static schema => schema.AnyOf(static arm => arm.Ref("Created"), static arm => arm.Ref("Deleted")))
            .WithSchema("Created", static schema => HoistedLeaf(schema, "created"))
            .WithSchema("Deleted", static schema => HoistedLeaf(schema, "deleted"))
            .WithSchema("Feed", static schema => schema.Type("object").Property("event", static property => property.Ref("ExampleEvent"))));
        var plan = new BindingTestHost().Bind(document, Selection("example.get"), curation with { Descriptions = [] });
        var targets = DescriptionTargetCatalog.Create(plan);
        await Assert.That(targets["IExampleEvent"].Order(StringComparer.Ordinal))
            .IsEquivalentTo(["Durable", "Id", "Note", "Type"], CollectionOrdering.Matching);
        await Assert.That(targets["UnknownExampleEvent"].Order(StringComparer.Ordinal)).IsEquivalentTo(["Payload", "Type"], CollectionOrdering.Matching);
        await Assert.That(targets["IExampleDurable"].Order(StringComparer.Ordinal)).IsEquivalentTo(["Seq"], CollectionOrdering.Matching);
        var members = plan.Unions.Single(static union => union.Name == "IExampleEvent").HoistedMembers;
        await Assert.That(members.Single(static member => member.Name == "Id").IsRequired).IsTrue();
        await Assert.That(members.Single(static member => member.Name == "Note").IsRequired).IsFalse();
        await Assert.That(members.Single(static member => member.Name == "Durable").IsRequired).IsTrue();
        var source = UnionEmitter.Emit(new BindingTestHost().Bind(document, Selection("example.get"), curation).Unions, plan.Placement, plan.Namespaces)
            .Single(static source => source.RelativePath.EndsWith("/IExampleEvent.cs", StringComparison.Ordinal));
        await Assert.That(Encoding.UTF8.GetString(source.Utf8Source.Span)).Contains(
            "Every declared variant carries it; an unrecognized variant preserved as UnknownExampleEvent returns null through this interface. An ordinary optional property may also be absent.");
    }

    private static Task<SpecDocument> IngestAsync(Action<SpecDocumentBuilder> arrange) =>
        BindingTestHost.IngestAsync(SpecScenario.Define(spec =>
        {
            arrange(spec);
            spec.WithOperation("example.get", configure: static operation => operation.Response(200, "application/json", static schema => schema.Ref("Feed")));
        }));

    private static SchemaBuilder Leaf(SchemaBuilder schema, string tag) => schema.Type("object")
        .Property("type", property => property.Type("string").Enum(tag), required: true);

    private static void HoistedLeaf(SchemaBuilder schema, string tag) => Leaf(schema, tag)
        .Property("id", static property => property.Type("string"), required: true)
        .Property("note", static property => property.Type("string"))
        .Property("durable", static property => property.Type("object").Property("seq", static member => member.Type("string"), required: true), required: true);
}
