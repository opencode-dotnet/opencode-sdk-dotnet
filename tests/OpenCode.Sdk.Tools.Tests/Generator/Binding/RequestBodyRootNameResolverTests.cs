using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// The request-body claim renames a component key, not one reference, so it may take only a root
/// that the claiming bodies alone reference; any other reachable referrer would carry the
/// operation's request name onto an unrelated wire role.
/// </summary>
public sealed class RequestBodyRootNameResolverTests
{
    private const string Root = "Widget.CreateInput";
    private const string NestedSize = Root + "#/properties/size";
    private const string InfoDraft = "Widget.Info#/properties/draft";
    private const string InfoSize = "Widget.Info#/properties/size";
    private const string Create = "widget.create";
    private const string Get = "widget.get";
    private const string RequestName = "WidgetCreateRequest";

    [Test]
    [Arguments("property", "schema 'Widget.Info'")]
    [Arguments("items", "schema 'Widget.Info'")]
    [Arguments("branch", "schema 'Widget.Draft'")]
    [Arguments("response", "operation 'widget.get'")]
    [Arguments("ordered", "operation 'widget.get', schema 'Widget.Info'")]
    public async Task Resolve_Should_Refuse_A_Body_Root_Another_Reachable_Schema_References(string referrer, string referrerName)
    {
        string[] selected = referrer is "response" or "ordered" ? [Create, Get] : [Create];

        var exception = await BindRefusalsAsync(BodyScenario(referrer), Curation(Groups("widget", RootGroup())), selected);

        await Assert.That(exception.Errors).IsEquivalentTo([SharedBodyRoot(referrerName)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Should_Claim_A_Body_Only_Root()
    {
        var plan = await BindAsync(BodyScenario("none"), Curation(Groups("widget", RootGroup())));

        await Assert.That(BodyTypeName(plan)).IsEqualTo(RequestName);
        await Assert.That(plan.Models.Select(static model => model.Name)).DoesNotContain("WidgetCreateInput");
    }

    [Test]
    public async Task Resolve_Should_Claim_A_Recursive_Body_Root()
    {
        var plan = await BindAsync(BodyScenario("recursive"), Curation(Groups("widget", RootGroup())));

        await Assert.That(BodyTypeName(plan)).IsEqualTo(RequestName);
        await Assert.That(PropertyTypeName(plan, RequestName, "child")).IsEqualTo(RequestName);
    }

    [Test]
    public async Task Resolve_Should_Keep_A_Nested_Schema_Of_The_Root_Shared()
    {
        var plan = await BindAsync(BodyScenario("nested"), Curation(Groups("widget", RootGroup())));

        await AssertSizeStaysSharedAsync(plan);
    }

    [Test]
    public async Task Resolve_Should_Ignore_A_Referrer_No_Selected_Operation_Reaches()
    {
        var plan = await BindAsync(BodyScenario("unselected"), Curation(Groups("widget", RootGroup())));

        await Assert.That(BodyTypeName(plan)).IsEqualTo(RequestName);
        await Assert.That(plan.Models.Select(static model => model.Name)).DoesNotContain("WidgetArchive");
    }

    [Test]
    public async Task Resolve_Should_Refuse_A_Body_Root_An_Alias_Rewrites_Onto()
    {
        var exception = await BindRefusalsAsync(BodyScenario("alias"),
            Curation(Groups("widget", RootGroup()), schemaAliases: [Alias(InfoDraft, Root)]), Create);

        await Assert.That(exception.Errors).IsEquivalentTo([SharedBodyRoot("schema 'Widget.Info'")], CollectionOrdering.Matching);
    }

    /// <summary>
    /// The pinned shape: an alias folds another request's size onto the root's nested size, which
    /// references the nested key and leaves the root itself referenced by its body alone.
    /// </summary>
    [Test]
    public async Task Resolve_Should_Claim_A_Root_Whose_Nested_Key_An_Alias_Targets()
    {
        var curation = Curation(Groups("widget", RootGroup()), schemaAliases: [Alias(InfoSize, NestedSize)],
            schemaNames: [SchemaName(NestedSize, "WidgetSize")]);

        var plan = await BindAsync(BodyScenario("nested-alias"), curation);

        await AssertSizeStaysSharedAsync(plan);
    }

    private static async Task AssertSizeStaysSharedAsync(EmitPlan plan)
    {
        await Assert.That(BodyTypeName(plan)).IsEqualTo(RequestName);
        await Assert.That(PropertyTypeName(plan, RequestName, "size")).IsEqualTo("WidgetSize");
        await Assert.That(PropertyTypeName(plan, "WidgetInfo", "size")).IsEqualTo("WidgetSize");
    }

    private static async Task<EmitPlan> BindAsync(SpecScenario scenario, GenerationCuration curation)
    {
        var document = await BindingTestHost.IngestAsync(scenario);

        return new BindingTestHost().Bind(document, Selection(Create), curation);
    }

    private static string BodyTypeName(EmitPlan plan) =>
        plan.Clients.SelectMany(static client => client.Operations).Single(static operation => operation.OperationId == Create).RequestBody!.TypeName;

    private static string PropertyTypeName(EmitPlan plan, string model, string wireName) =>
        ((NamedTypeReferencePlan)plan.Models.OfType<ObjectModelPlan>().Single(candidate => candidate.Name == model)
            .Properties.Single(property => property.WireName == wireName).Type).Name;

    /// <summary>
    /// The body root and one referrer shape per case; <c>widget.get</c> is selected only where a
    /// case says so, so its schemas are reachable only then.
    /// </summary>
    private static SpecScenario BodyScenario(string referrer) => SpecScenario.Define(spec =>
    {
        _ = spec
            .WithSchema(Root, schema => _ = referrer switch
            {
                "nested" => Titled(schema).Property("size", static property => property.Ref("Widget.Size"), required: true),
                "nested-alias" => Titled(schema).Property("size", Size, required: true),
                "recursive" => Titled(schema).Property("child", static property => property.Ref(Root), required: true),
                _ => Titled(schema),
            })
            .WithSchema("Widget.Size", Size)
            .WithSchema("Widget.Card", static schema => schema
                .Type("object")
                .Property("label", static property => property.Type("string"), required: true))
            .WithSchema("Widget.Draft", static schema => schema.AnyOf(static branch => branch.Ref(Root), static branch => branch.Ref("Widget.Card")))
            .WithSchema("Widget.Archive", static schema => schema
                .Type("object")
                .Property("draft", static property => property.Ref(Root), required: true))
            .WithSchema("Widget.Info", schema => Referrer(schema.Type("object").Property("id", static property => property.Type("string"), required: true), referrer))
            .WithOperation(Create, method: "post", path: "/api/widget", configure: static operation => operation
                .RequestBody("application/json", static body => body.Ref(Root), required: true)
                .Response(200, "application/json", static schema => schema.Ref("Widget.Info")))
            .WithOperation(Get, path: "/api/widget/archive", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref(referrer is "response" or "ordered" ? Root : "Widget.Archive")));
    });

    private static void Referrer(SchemaBuilder info, string referrer) => _ = referrer switch
    {
        "property" or "ordered" => info.Property("draft", static property => property.Ref(Root), required: true),
        "items" => info.Property("drafts", static property => property.Type("array").Items(static item => item.Ref(Root)), required: true),
        "branch" => info.Property("draft", static property => property.Ref("Widget.Draft"), required: true),
        "alias" => info.Property("draft", static property => Titled(property), required: true),
        "nested" => info.Property("size", static property => property.Ref("Widget.Size"), required: true),
        "nested-alias" => info.Property("size", Size, required: true),
        _ => info,
    };

    private static SchemaBuilder Titled(SchemaBuilder schema) => schema
        .Type("object")
        .Property("title", static property => property.Type("string"), required: true);

    private static void Size(SchemaBuilder schema) => schema
        .Type("object")
        .Property("width", static property => property.Type("integer"), required: true);

    private static BindingError SharedBodyRoot(string referrer) =>
        new(BindingErrorCategory.Naming, Root, $"request-body claim cannot rename a key also referenced by {referrer}");
}
