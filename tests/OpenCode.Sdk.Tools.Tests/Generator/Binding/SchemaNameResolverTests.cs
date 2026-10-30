using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// Inline unions take pointer names, and ordinal-derived names need explicit curation so branch
/// movement cannot silently rename a type.
/// </summary>
public sealed class SchemaNameResolverTests
{
    private const string OperationId = "widget.get";
    private const string Detail = "Widget.Info#/properties/detail";
    private const string FirstBranch = Detail + "/anyOf/0";
    private const string SecondBranch = Detail + "/anyOf/1";
    private const string CardName = "WidgetDetailCard";
    private const string TileName = "WidgetDetailTile";
    private const string ComponentUnion = "Widget.Detail";
    private const string ResponseUnion = "op:widget.get#/responses/201/content/application~1json/schema";

    [Test]
    [Arguments(true, "IWidgetServerStatus", "UnknownWidgetServerStatus")]
    [Arguments(false, "WidgetServerStatus", "WidgetServerStatusKind")]
    public async Task Resolve_Should_Keep_An_Inline_Union_Name_When_A_Branch_Is_Added(bool marked, string name, string companion)
    {
        var before = await BindServerAsync(marked, addedBranch: false);
        var after = await BindServerAsync(marked, addedBranch: true);

        await Assert.That(StatusNames(before)).IsEquivalentTo([name, companion], CollectionOrdering.Matching);
        await Assert.That(StatusNames(after)).IsEquivalentTo([name, companion], CollectionOrdering.Matching);
    }

    /// <summary>
    /// The refused key keeps its mechanical name, so later bind stages report no secondary
    /// unnamed-type errors and the pending-operation probe sees only a missing row.
    /// </summary>
    [Test]
    [Arguments("anyOf", false, SecondBranch, "WidgetInfoDetail1")]
    [Arguments("oneOf", false, Detail + "/oneOf/1", "WidgetInfoDetail1")]
    [Arguments("anyOf", true, SecondBranch + "/properties/kind", "WidgetInfoDetail1Kind")]
    public async Task Resolve_Should_Refuse_An_Ordinal_Derived_Name_Without_A_Row(string keyword, bool descendant, string key,
        string mechanicalName)
    {
        var errors = new BindingErrorCollector();
        SchemaNameCuration[] rows = descendant ? [SchemaName(SecondBranch, CardName)] : [];
        var scenario = DetailScenario(keyword, Text, descendant ? KindedCard : Card);

        var names = await ResolveAsync(scenario, errors, rows);

        await Assert.That(errors.Snapshot()).IsEquivalentTo([MissingOrdinalRow(key, mechanicalName)], CollectionOrdering.Matching);
        await Assert.That(names[key]).IsEqualTo(mechanicalName);
        var exception = await BindRefusalsAsync(scenario, Curation(Groups("widget", RootGroup()), schemaNames: rows), OperationId);
        await Assert.That(exception.Errors).IsEquivalentTo([MissingOrdinalRow(key, mechanicalName)], CollectionOrdering.Matching);
    }

    /// <summary>
    /// An inline object branch with a required key no sibling requires is keyed by that key, so
    /// its mechanical name holds in either order and needs no row.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Resolve_Should_Key_An_Inline_Branch_By_Its_Own_Required_Key_In_Either_Order(bool reordered)
    {
        var errors = new BindingErrorCollector();
        Action<SchemaBuilder> inArm = static schema => schema.Type("object").Property("in", static property => property.Type("string"), required: true);
        Action<SchemaBuilder> atArm = static schema => schema.Type("object").Property("at", static property => property.Type("string"), required: true);

        var names = await ResolveAsync(DetailScenario("anyOf", reordered ? [atArm, inArm] : [inArm, atArm]), errors);

        await Assert.That(errors.Snapshot()).IsEmpty();
        await Assert.That((names[Detail + "/anyOf/in"], names[Detail + "/anyOf/at"])).IsEqualTo(("WidgetInfoDetailIn", "WidgetInfoDetailAt"));
    }

    /// <summary>A key a sibling also requires, even behind a reference, keys nothing.</summary>
    [Test]
    [Arguments("serial", false)]
    [Arguments("label", true)]
    public async Task Resolve_Should_Keep_An_Ordinal_When_No_Required_Key_Is_The_Branch_Own(string key, bool shared)
    {
        var errors = new BindingErrorCollector();
        Action<SchemaBuilder> arm = schema => schema.Type("object").Property(key, static property => property.Type("string"), required: true);
        var sibling = shared ? arm : Gadget;

        var names = await ResolveAsync(DetailScenario("anyOf", sibling, arm), errors);

        await Assert.That(names.Keys).Contains(SecondBranch);
    }

    [Test]
    public async Task Resolve_Should_Name_An_Ordinal_Branch_By_Its_Row()
    {
        var errors = new BindingErrorCollector();

        var names = await ResolveAsync(DetailScenario("anyOf", Text, Card), errors, SchemaName(SecondBranch, CardName));

        await Assert.That(errors.Snapshot()).IsEmpty();
        await Assert.That(names[SecondBranch]).IsEqualTo(CardName);
    }

    /// <summary>
    /// Two ordinal branches can trade places with every key still valid, so their rows would
    /// silently retarget; the union refuses even though both rows exist.
    /// </summary>
    [Test]
    [Arguments(Detail, Detail + "/anyOf/")]
    [Arguments(ComponentUnion, ComponentUnion + "#/anyOf/")]
    [Arguments(ResponseUnion, ResponseUnion + "/anyOf/")]
    public async Task Resolve_Should_Refuse_A_Union_With_Two_Ordinal_Branches(string union, string branchPrefix)
    {
        var errors = new BindingErrorCollector();

        _ = await ResolveAsync(SiblingScenario(union), errors,
            SchemaName(branchPrefix + "0", CardName),
            SchemaName(branchPrefix + "1", TileName));

        await Assert.That(errors.Snapshot()).IsEquivalentTo([OrdinalSiblings(union, branchPrefix + "0", branchPrefix + "1")], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Name_A_Union_With_One_Ordinal_Branch_Beside_A_Component()
    {
        var errors = new BindingErrorCollector();

        var names = await ResolveAsync(DetailScenario("anyOf", Card, Gadget), errors, SchemaName(FirstBranch, CardName));

        await Assert.That(errors.Snapshot()).IsEmpty();
        await Assert.That(names[Detail]).IsEqualTo("WidgetInfoDetail");
        await Assert.That(names[FirstBranch]).IsEqualTo(CardName);
    }

    /// <summary>
    /// Moving the single ordinal branch changes its key: the old row names a key the document no
    /// longer promotes, and the new key has no row. Neither half renames silently.
    /// </summary>
    [Test]
    public async Task Resolve_Should_Refuse_When_The_Single_Ordinal_Branch_Moves()
    {
        var curation = Curation(Groups("widget", RootGroup()), schemaNames: [SchemaName(FirstBranch, CardName)]);
        var placed = await BindingTestHost.IngestAsync(DetailScenario("anyOf", Card, Text));

        var plan = new BindingTestHost().Bind(placed, Selection(OperationId), curation);
        var exception = await BindRefusalsAsync(DetailScenario("anyOf", Text, Card), curation, OperationId);

        await Assert.That(plan.Models).Contains(static model => model.Name == CardName);
        await Assert.That(exception.Errors).IsEquivalentTo(
        [
            new BindingError(BindingErrorCategory.Curation, FirstBranch, "curated schema does not exist in the spec"),
            MissingOrdinalRow(SecondBranch, "WidgetInfoDetail1"),
        ], CollectionOrdering.Matching);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(SpecScenario scenario, BindingErrorCollector errors,
        params SchemaNameCuration[] rows)
    {
        var document = await BindingTestHost.IngestAsync(scenario);
        IReadOnlyList<SpecOperation> selected = [.. document.Operations];
        var reachable = ReachableSchemaCollector.Collect(document, selected, errors);

        return SchemaNameResolver.Resolve(document, reachable, selected, Curation(Groups(), schemaNames: rows), errors);
    }

    private static async Task<EmitPlan> BindServerAsync(bool marked, bool addedBranch)
    {
        var document = await BindingTestHost.IngestAsync(ServerScenario(marked, addedBranch));

        return new BindingTestHost().Bind(document, Selection(OperationId), Curation(Groups("widget", RootGroup())));
    }

    private static string[] StatusNames(EmitPlan plan)
    {
        var server = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "WidgetServer");
        var status = ((NamedTypeReferencePlan)server.Properties.Single(static property => property.WireName == "status").Type).Name;
        var companion = plan.Unions.SingleOrDefault(union => union.Name == status)?.UnknownTypeName
                        ?? plan.Models.OfType<StructuralUnionModelPlan>().Single(model => model.Name == status).KindTypeName;

        return [status, companion];
    }

    /// <summary>
    /// A marked arm carries a status literal; a structural arm has its own required key. The
    /// initial components share a name prefix that the added branch does not share.
    /// </summary>
    private static SpecScenario ServerScenario(bool marked, bool addedBranch) => SpecScenario.Define(spec =>
    {
        _ = spec
            .WithSchema("Widget.Status.Idle", schema => StatusArm(schema, marked, "idle", "since"))
            .WithSchema("Widget.Status.Busy", schema => StatusArm(schema, marked, "busy", "job"))
            .WithSchema("Gadget.Paused", schema => StatusArm(schema, marked, "paused", "until"));
        Action<SchemaBuilder>[] branches = addedBranch
            ? [Reference("Widget.Status.Idle"), Reference("Widget.Status.Busy"), Reference("Gadget.Paused")]
            : [Reference("Widget.Status.Idle"), Reference("Widget.Status.Busy")];
        _ = spec
            .WithSchema("Widget.Server", schema => schema
                .Type("object")
                .Property("status", property => property.AnyOf(branches), required: true))
            .WithOperation(OperationId, configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Widget.Server")));
    });

    private static void StatusArm(SchemaBuilder schema, bool marked, string status, string requiredKey)
    {
        _ = schema
            .Type("object")
            .Property(requiredKey, static property => property.Type("string"), required: true);

        _ = marked
            ? schema.Property("status", property => property.Type("string").Enum(status), required: true)
            : schema;
    }

    private static Action<SchemaBuilder> Reference(string target) => schema => schema.Ref(target);

    private static SpecScenario DetailScenario(string keyword, params Action<SchemaBuilder>[] branches) => SpecScenario.Define(spec => spec
        .WithSchema("Gadget.Info", static schema => schema
            .Type("object")
            .Property("serial", static property => property.Type("string"), required: true))
        .WithSchema("Widget.Info", schema => schema
            .Type("object")
            .Property("detail", property => _ = keyword == "oneOf" ? property.OneOf(branches) : property.AnyOf(branches), required: true))
        .WithOperation(OperationId, configure: static operation => operation
            .Response(200, "application/json", static schema => schema.Ref("Widget.Info"))));

    private static SpecScenario SiblingScenario(string union) => union switch
    {
        ComponentUnion => SpecScenario.Define(static spec => spec
            .WithSchema(ComponentUnion, static schema => schema.AnyOf(Card, Tile))
            .WithOperation(OperationId, configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref(ComponentUnion)))),
        ResponseUnion => SpecScenario.Define(static spec => spec.WithOperation(OperationId, configure: static operation => operation
            .Response(201, "application/json", static schema => schema.AnyOf(Card, Tile)))),
        _ => DetailScenario("anyOf", Card, Tile),
    };

    private static void Text(SchemaBuilder schema) => schema.Type("string");

    private static void Card(SchemaBuilder schema) => schema
        .Type("object")
        .Property("label", static property => property.Type("string"));

    private static void KindedCard(SchemaBuilder schema) => schema
        .Type("object")
        .Property("label", static property => property.Type("string"))
        .Property("kind", static property => property.Type("string").Enum("small", "large"));

    private static void Tile(SchemaBuilder schema) => schema
        .Type("object")
        .Property("size", static property => property.Type("integer"));

    private static void Gadget(SchemaBuilder schema) => schema.Ref("Gadget.Info");

    private static BindingError MissingOrdinalRow(string key, string mechanicalName) =>
        new(BindingErrorCategory.Curation, key,
            $"schema '{key}' has mechanical name '{mechanicalName}' that spells a union branch ordinal; add a schemaNames row that names the schema")
        {
            Kind = BindingErrorKind.MissingRow,
        };

    private static BindingError OrdinalSiblings(string union, string first, string second) =>
        new(BindingErrorCategory.Naming, union,
            $"union carries more than one inline branch identified by ordinal ('{first}', '{second}'); a reorder would swap their names silently");
}
