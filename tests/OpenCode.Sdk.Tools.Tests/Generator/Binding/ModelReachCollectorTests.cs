using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Generator.Binding.Support.OwnershipPlanData;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// Reach through the real collector over an ingested document: a schema-backed model is reached by
/// the families whose selected operations reach its schema; a union interface and its unknown
/// carrier by every family reaching one of its arms; a structural union's kind by its union's
/// families; a hoisted interface by the families reaching the records implementing it. Every
/// families list is ordinal-sorted.
/// </summary>
public sealed class ModelReachCollectorTests
{
    private const string Sessions = "Sessions";
    private const string McpServers = "McpServers";
    private const string Websearch = "Websearch";
    private const string WebSockets = "WebSockets";
    private const string Widget = "Widget";
    private const string Gadget = "Gadget";
    private const string Gizmo = "Gizmo";
    private const string Sprocket = "Sprocket";
    private const string SessionWidget = "session.widget";
    private const string SessionGadget = "session.gadget";
    private const string McpGadget = "mcp.gadget";
    private const string McpGizmo = "mcp.gizmo";
    private const string WebsearchSprocket = "websearch.sprocket";
    private const string WebSocketSprocket = "websocket.sprocket";
    private const string PartConcept = "Part";
    private const string PartInterface = "IPart";
    private const string UnknownPart = "UnknownPart";
    private const string GadgetKind = "GadgetKind";
    private const string HoistedShape = "IWidgetShape";
    private const string Marker = "type";
    private const string GizmoPrefix = "gizmo.";
    private const string JsonMediaType = "application/json";
    private const string ClientNamespace = "OpenCode.Sdk";
    private const char FamilySeparator = ',';

    private static readonly Dictionary<string, string> FamilyOf = new(StringComparer.Ordinal)
    {
        [SessionWidget] = Sessions,
        [SessionGadget] = Sessions,
        [McpGadget] = McpServers,
        [McpGizmo] = McpServers,
        [WebsearchSprocket] = Websearch,
        [WebSocketSprocket] = WebSockets,
    };

    private static readonly Dictionary<string, string> ModelOf = new(StringComparer.Ordinal)
    {
        [SessionWidget] = Widget,
        [SessionGadget] = Gadget,
        [McpGadget] = Gadget,
        [McpGizmo] = Gizmo,
        [WebsearchSprocket] = Sprocket,
        [WebSocketSprocket] = Sprocket,
    };

    [Test]
    public async Task Collect_Should_List_The_One_Family_Whose_Operations_Reach_A_Model()
    {
        var reach = await CollectAsync(Candidates(), SessionWidget, McpGizmo);

        await Assert.That(ReachOf(reach, Widget)).IsEqualTo(InOrder(Sessions));
        await Assert.That(ReachOf(reach, Gizmo)).IsEqualTo(InOrder(McpServers));
    }

    [Test]
    public async Task Collect_Should_List_Both_Families_In_Ordinal_Order_When_Two_Families_Reach_A_Model()
    {
        var reach = await CollectAsync(Candidates(), SessionGadget, McpGadget);

        await Assert.That(ReachOf(reach, Gadget)).IsEqualTo(InOrder(McpServers, Sessions));
    }

    [Test]
    public async Task Collect_Should_Give_A_Union_Interface_And_Its_Unknown_Carrier_The_Reach_Of_Its_Arms()
    {
        var part = Union(PartConcept) with { Variants = [Arm(Widget), Arm(Gizmo)] };

        var reach = await CollectAsync(Candidates(unions: [part]), SessionWidget, McpGizmo);

        await Assert.That(ReachOf(reach, PartInterface)).IsEqualTo(InOrder(McpServers, Sessions));
        await Assert.That(ReachOf(reach, UnknownPart)).IsEqualTo(InOrder(McpServers, Sessions));
    }

    [Test]
    public async Task Collect_Should_Give_A_Union_Interface_The_Reach_Of_Its_Prefix_Arm()
    {
        var part = Union(PartConcept) with
        {
            Variants = [Arm(Widget)],
            PrefixVariant = new UnionPrefixVariantPlan
            {
                TypeName = Gizmo,
                Prefix = GizmoPrefix,
                MarkerWireName = Marker,
            },
        };

        var reach = await CollectAsync(Candidates(unions: [part]), SessionWidget, McpGizmo);

        await Assert.That(ReachOf(reach, PartInterface)).IsEqualTo(InOrder(McpServers, Sessions));
    }

    [Test]
    public async Task Collect_Should_Propagate_Nested_Union_Arm_Reach_Regardless_Of_Plan_Order()
    {
        var outer = Union("Outer") with { Variants = [Arm(PartInterface) with { IsNestedUnion = true }] };
        var inner = Union(PartConcept) with { Variants = [Arm(Widget)] };

        var reach = await CollectAsync(Candidates(unions: [outer, inner]), SessionWidget);

        await Assert.That(ReachOf(reach, "IOuter")).IsEqualTo(InOrder(Sessions));
        await Assert.That(ReachOf(reach, "UnknownOuter")).IsEqualTo(InOrder(Sessions));
    }

    [Test]
    public async Task Collect_Should_Give_A_Structural_Union_Kind_The_Reach_Of_Its_Union()
    {
        var reach = await CollectAsync(Candidates(models: [StructuralUnion(Gadget, GadgetKind)]), SessionGadget, McpGadget);

        await Assert.That(ReachOf(reach, GadgetKind)).IsEqualTo(InOrder(McpServers, Sessions));
    }

    [Test]
    public async Task Collect_Should_Give_A_Hoisted_Interface_The_Reach_Of_The_Records_Implementing_It()
    {
        var shape = HoistedInterface(HoistedShape) with { ImplementedBy = [Widget, Gizmo] };

        var reach = await CollectAsync(Candidates(hoisted: [shape]), SessionWidget, McpGizmo);

        await Assert.That(ReachOf(reach, HoistedShape)).IsEqualTo(InOrder(McpServers, Sessions));
    }

    /// <summary>
    /// The selection names <c>Websearch</c> first and a culture-aware sort would keep it first;
    /// only an ordinal sort puts <c>WebSockets</c> ahead.
    /// </summary>
    [Test]
    public async Task Collect_Should_Sort_The_Families_Ordinally()
    {
        var reach = await CollectAsync(Candidates(), WebsearchSprocket, WebSocketSprocket);

        await Assert.That(ReachOf(reach, Sprocket)).IsEqualTo(InOrder(WebSockets, Websearch));
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> CollectAsync(OwnershipCandidates candidates,
        params string[] operationIds)
    {
        var errors = new BindingErrorCollector();
        var document = await BindingTestHost.IngestAsync(ReachScenario());
        var operationsById = document.Operations.ToDictionary(static operation => operation.OperationId, StringComparer.Ordinal);
        IReadOnlyList<SpecOperation> selected = [.. operationIds.Select(operationId => operationsById[operationId])];
        var reachable = ReachableSchemaCollector.Collect(document, selected, errors);
        var typeNames = SchemaNameResolver.Resolve(document, reachable, selected, Curation(Groups()), errors);

        var reach = ModelReachCollector.Collect(new ReachScope(document, selected, Clients(selected), typeNames),
            candidates, errors);

        errors.ThrowIfAny();
        return reach;
    }

    private static SpecScenario ReachScenario() =>
        SpecScenario.Define(static spec =>
        {
            foreach (var model in ModelOf.Values.Distinct(StringComparer.Ordinal))
            {
                _ = spec.WithSchema(model, static schema => schema
                    .Type("object")
                    .Property("id", static property => property.Type("string"), required: true));
            }

            foreach (var (operationId, model) in ModelOf)
            {
                _ = spec.WithOperation(operationId, path: RouteOf(operationId), configure: operation => operation
                    .Response(200, JsonMediaType, schema => schema.Ref(model)));
            }
        });

    private static IReadOnlyList<ClientPlan> Clients(IReadOnlyList<SpecOperation> selected) =>
    [
        .. selected
            .GroupBy(static operation => FamilyOf[operation.OperationId], StringComparer.Ordinal)
            .Select(static family => new ClientPlan
            {
                Name = family.Key,
                Namespace = ClientNamespace,
                Role = ClientRole.Collection,
                SubClients = [],
                Operations = [.. family.Select(operation => Operation(operation.OperationId, family.Key))],
            }),
    ];

    private static OperationPlan Operation(string operationId, string family) =>
        new()
        {
            OperationId = operationId,
            MethodName = "GetAsync",
            HttpMethod = "get",
            RouteTemplate = RouteOf(operationId),
            RouteContainerName = family,
            RouteMemberName = "Get",
            Parameters = [],
            ErrorMap = new ErrorMapPlan { Statuses = [] },
        };

    private static OwnershipCandidates Candidates(IReadOnlyList<ModelPlan>? models = null, IReadOnlyList<UnionPlan>? unions = null,
        IReadOnlyList<HoistedInterfacePlan>? hoisted = null) =>
        new()
        {
            Models = models ?? [],
            Unions = unions ?? [],
            HoistedInterfaces = hoisted ?? [],
        };

    private static UnionVariantPlan Arm(string typeName) =>
        new()
        {
            TypeName = typeName,
            Tag = typeName,
            MarkerWireName = Marker,
        };

    private static string RouteOf(string operationId) => $"/api/{operationId.Replace('.', '/')}";

    private static string ReachOf(IReadOnlyDictionary<string, IReadOnlyList<string>> reach, string typeName) =>
        string.Join(FamilySeparator, reach[typeName]);

    private static string InOrder(params string[] families) => string.Join(FamilySeparator, families);
}
