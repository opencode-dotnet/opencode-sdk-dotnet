using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class OperationPlanBinderTests
{
    private const string RootClient = "OpenCodeClient";

    private static readonly string[] ClaimOrderModelNames = ["WidgetsClient", "WidgetListResponse", "WidgetStreamAdapter", "WidgetListRequest"];

    [Test]
    [Arguments("kind", "ShapeKind")]
    [Arguments("unknown", "UnknownEvent")]
    [Arguments("adapter", "WidgetListResponseAdapter")]
    [Arguments("model", "SharedModel")]
    [Arguments("union converter", "EventJsonConverter")]
    [Arguments("carrier converter", "UnknownEventJsonConverter")]
    [Arguments("structural converter", "ShapeJsonConverter")]
    [Arguments("enum converter", "PhaseJsonConverter")]
    [Arguments("routes", "OpenCodeRoutes")]
    [Arguments("context", "OpenCodeJsonContext")]
    public async Task CheckTypeNameCollisions_Should_Refuse_A_Model_Against_Another_Generated_Declaration(string declaration, string name)
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(static operation => operation.WithoutResponse(204)));
        var clients = declaration == "adapter" ? BindWidgets(document).Clients : [];
        ModelPlan[] models = declaration switch
        {
            "kind" => [Support.OwnershipPlanData.StructuralUnion("Shape", name)],
            "structural converter" => [Support.OwnershipPlanData.StructuralUnion("Shape", "ShapeKind")],
            "enum converter" => [Support.OwnershipPlanData.Enum("Phase")],
            "model" => [Support.OwnershipPlanData.Model(name)],
            _ => [],
        };
        var candidates = new OwnershipCandidates
        {
            Models = [.. models, Support.OwnershipPlanData.Model(name)],
            Unions = declaration is "unknown" or "union converter" or "carrier converter" ? [Support.OwnershipPlanData.Union("Event")] : [],
            HoistedInterfaces = [],
        };
        await Assert.That(NameCollisions(candidates, clients)).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, name, $"type name '{name}' collides with another generated type"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Preserve_The_Mixed_Request_Wall_After_A_Query_Facet_Refuses()
    {
        var document = await BindingTestHost.IngestAsync(MixedRequestScenario(static operation =>
            operation.Parameter("broken", "query", static schema => schema.Type("integer"))));
        var exception = Assert.Throws<BindingException>(() => BindWidgets(document, "widget.create", strings: ["search"]));

        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Operation, "widget.create", "query parameter 'broken' has an unsupported schema shape"),
            new BindingError(BindingErrorCategory.Operation, "widget.create", "operations mixing a request body and query parameters are supported only for the location selector"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Preserve_Enumeration_Naming_After_A_Query_Facet_Refuses()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: static operation =>
            CursorQuery(operation).Parameter("broken", "query", static schema => schema.Type("integer"))));
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("widget.list"),
            Curation(Groups("widget", ClientGroup("Widgets", null, null)),
                operationNames: [OperationName("widget.list", "BrowseWidgetsAsync")],
                queryParameterTypes: [QueryParameterType("widget.list", "limit", "int")],
                stringQueryParameters: [StringQueryParameter("widget.list", "cursor")])));

        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Operation, "widget.list", "query parameter 'broken' has an unsupported schema shape"),
            new BindingError(BindingErrorCategory.Naming, "widget.list", "cursor-list method 'BrowseWidgetsAsync' must be an asynchronous List method before its enumeration name can be derived"),
        ], CollectionOrdering.Matching);
    }

    private static OperationBuilder CursorQuery(OperationBuilder operation) => operation
        .Parameter("limit", "query", QueryScenarioData.NullableString)
        .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)
        .Parameter("cursor", "query", QueryScenarioData.NullableString);

    [Test]
    public async Task CheckTypeNameCollisions_Should_Keep_An_Alias_Name_In_The_Schema_Claim_Phase()
    {
        var candidates = new OwnershipCandidates { Models = [], Unions = [], HoistedInterfaces = [] };
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["Alias"] = "ListCursor" };
        await Assert.That(NameCollisions(candidates, [], names)).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "Alias", "model type name 'ListCursor' collides with the hand-written spine type 'ListCursor'"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CheckTypeNameCollisions_Should_Refuse_Two_Derived_Declarations_With_One_Name()
    {
        var candidates = new OwnershipCandidates
        {
            Models = [Support.OwnershipPlanData.StructuralUnion("Shape", "SharedKind")],
            Unions = [Support.OwnershipPlanData.Union("Event") with { UnknownTypeName = "SharedKind" }],
            HoistedInterfaces = [],
        };
        await Assert.That(NameCollisions(candidates, [],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Shape"] = "Shape", ["Event"] = "IEvent" })).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "IEvent", "type name 'SharedKind' collides with another generated type"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("enum", "Status", "StatusJsonConverter")]
    [Arguments("carrier", "IUnknownStatus", "UnknownStatusJsonConverter")]
    [Arguments("structural", "Status", "StatusJsonConverter")]
    public async Task CheckTypeNameCollisions_Should_Refuse_Two_Converters_With_One_Name(string declaration, string subject, string converter)
    {
        var candidates = new OwnershipCandidates
        {
            Models = declaration switch
            {
                "enum" => [Support.OwnershipPlanData.Enum("Status")],
                "structural" => [Support.OwnershipPlanData.StructuralUnion("Status", "StatusKind")],
                _ => [],
            },
            Unions = declaration == "carrier"
                ? [Support.OwnershipPlanData.Union("Status"), Support.OwnershipPlanData.Union("UnknownStatus")]
                : [Support.OwnershipPlanData.Union("Status")],
            HoistedInterfaces = [],
        };

        await Assert.That(NameCollisions(candidates, [])).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, subject, $"type name '{converter}' collides with another generated type"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CheckTypeNameCollisions_Should_Claim_One_Optional_Converter_Per_Instantiation()
    {
        var fooList = UnionHoistPlanData.Named("FooList", isNullable: true);
        var listOfFoo = new ListTypeReferencePlan { ElementType = UnionHoistPlanData.Named("Foo"), IsNullable = true, JsonNullRepresentation = JsonNullRepresentation.ClrNull };

        var shared = NameCollisions(OptionalHolder(fooList, fooList), []);
        var distinct = NameCollisions(OptionalHolder(fooList, listOfFoo), []);

        await Assert.That(shared).IsEmpty();
        await Assert.That(distinct).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "IReadOnlyList<Foo>?", "type name 'OptionalOfFooListJsonConverter' collides with another generated type"),
        ], CollectionOrdering.Matching);
    }

    /// <summary>One record whose two optional members wrap the given types.</summary>
    private static OwnershipCandidates OptionalHolder(TypeReferencePlan first, TypeReferencePlan second) =>
        new()
        {
            Models =
            [
                Support.OwnershipPlanData.Model("Holder") with
                {
                    Properties =
                    [
                        UnionHoistPlanData.Property("first", first, isRequired: false, emitsOptionalWrapper: true),
                        UnionHoistPlanData.Property("second", second, isRequired: false, emitsOptionalWrapper: true),
                    ],
                },
            ],
            Unions = [],
            HoistedInterfaces = [],
        };

    private static IReadOnlyList<BindingError> NameCollisions(OwnershipCandidates candidates, IReadOnlyList<ClientPlan> clients,
        IReadOnlyDictionary<string, string>? schemaNames = null)
    {
        var inventory = GeneratedTypeNameInventory.Create(candidates, clients, [], schemaNames ?? new Dictionary<string, string>(StringComparer.Ordinal));
        var errors = new BindingErrorCollector();
        OperationPlanBinder.CheckTypeNameCollisions(inventory, clients, errors);
        return errors.Snapshot();
    }

    [Test]
    public async Task Bind_Should_Preserve_Type_Name_Claim_Order()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(static operation => operation
            .Parameter("count", "query", static schema => schema.Type("string").Enum("true", "false"))));
        var plan = BindWidgets(document);
        var client = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        var first = client.Operations.Single();
        var second = first with { OperationId = "widget.list.get" };
        var stream = first with
        {
            OperationId = "widget.watch",
            Envelope = null,
            QueryRequest = null,
            Stream = new StreamPlan { AdapterTypeName = "WidgetStreamAdapter", PayloadTypeName = "WidgetInfo", FailureEventName = "error", CauseTypeName = "WidgetCause[]" }
        };
        var clients = new[] { client with { Operations = [stream, second, first] } };
        var candidates = new OwnershipCandidates
        {
            Models = [.. ClaimOrderModelNames
            .Select(static name => new ObjectModelPlan { Name = name, Namespace = GeneratedNamespace.Models, Properties = [] })],
            Unions = [],
            HoistedInterfaces = []
        };
        var errors = new BindingErrorCollector();
        OperationPlanBinder.CheckTypeNameCollisions(GeneratedTypeNameInventory.Create(candidates, clients, [], new Dictionary<string, string>(StringComparer.Ordinal)), clients, errors);
        await Assert.That(errors.Snapshot()).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "WidgetsClient", "client type name 'WidgetsClient' collides with another generated type"),
            new BindingError(BindingErrorCategory.Naming, "widget.list", "multiple operations map to response type 'WidgetListResponse'"),
            new BindingError(BindingErrorCategory.Naming, "widget.list.get", "multiple operations map to response type 'WidgetListResponse'"),
            new BindingError(BindingErrorCategory.Naming, "widget.watch", "multiple operations map to stream adapter 'WidgetStreamAdapter'"),
            new BindingError(BindingErrorCategory.Naming, "widget.list", "request type name 'WidgetListRequest' collides with another generated type"),
            new BindingError(BindingErrorCategory.Naming, "widget.list.get", "request type name 'WidgetListRequest' collides with another generated type"),
            new BindingError(BindingErrorCategory.Naming, "widget.list.get", "multiple operations map to route member 'Widgets.ListWidgets'"),
            new BindingError(BindingErrorCategory.Naming, "widget.watch", "multiple operations map to route member 'Widgets.ListWidgets'"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("post")]
    [Arguments("binary")]
    [Arguments("route")]
    public async Task Bind_Should_Omit_Only_The_Cursor_Companion_When_The_Signature_Is_Incompatible(string signature)
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: operation =>
        {
            CursorQuery(operation);
            if (signature == "binary")
            {
                operation.RequestBody("application/octet-stream", static schema => schema.Type("string").Format("binary"), required: true);
            }
            if (signature == "route")
            {
                operation.Parameter("id", "path", static schema => schema.Type("string"), required: true);
            }
        }, method: signature == "route" ? "get" : "post", path: signature == "route" ? "/api/widget/{id}" : "/api/widget"));
        var list = BindWidgetList(document);
        await Assert.That(list.MethodName).IsEqualTo("ListWidgetsAsync");
        await Assert.That(list.QueryRequest!.DerivesFromListRequest).IsTrue();
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.WireName))
            .IsEquivalentTo(["limit", "order", "cursor"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(list.Pagination).IsNull();
        if (signature == "binary")
        {
            await Assert.That(list.RequestBody!.IsBinary).IsTrue();
        }
        else
        {
            await Assert.That(list.RequestBody).IsNull();
        }
        await Assert.That(list.Parameters.Select(static parameter => parameter.WireName))
            .IsEquivalentTo(signature == "route" ? ["id"] : [], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Reject_A_Refused_Facet_Even_When_The_Error_Is_A_Missing_Row()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(static operation => operation
            .Parameter("search", "query", static schema => schema.Type("string"))));
        var exception = Assert.Throws<BindingException>(() => BindWidgets(document));
        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Curation, "widget.list:search",
                "the query parameter is a plain string that no curation row classifies: add a queryParameterTypes row when upstream decodes it as a number, or a stringQueryParameters row that says why it stays a string") { Kind = BindingErrorKind.MissingRow },
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Preserve_Independent_Facet_Refusals_In_Binder_Order()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: static operation => CursorQuery(operation)
            .Response(400, "text/plain", static schema => schema.Type("string"))));
        var curation = Curation(Groups("widget", ClientGroup("Widgets", null, null)),
            operationNames: [OperationName("widget.list", "BrowseWidgetsAsync")],
            queryParameterTypes: [QueryParameterType("widget.list", "limit", "int")],
            stringQueryParameters: [StringQueryParameter("widget.list", "cursor")]);
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("widget.list"), curation));
        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Operation, "widget.list", "error responses must carry a JSON schema"),
            new BindingError(BindingErrorCategory.Naming, "widget.list", "cursor-list method 'BrowseWidgetsAsync' must be an asynchronous List method before its enumeration name can be derived"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Mixed_Body_And_Nonlocation_Query_Explicitly()
    {
        var document = await BindingTestHost.IngestAsync(MixedRequestScenario());
        var exception = Assert.Throws<BindingException>(() => BindWidgets(document, "widget.create", strings: ["search"]));
        await Assert.That(exception.Errors)
            .IsEquivalentTo([new BindingError(BindingErrorCategory.Operation, "widget.create", "operations mixing a request body and query parameters are supported only for the location selector")], CollectionOrdering.Matching);
    }

    private static SpecScenario MixedRequestScenario(Action<OperationBuilder>? configure = null) => SpecScenario.Define(spec => spec
        .WithOperation("widget.create", method: "post", configure: operation =>
        {
            operation.WithoutResponse(200).Response(204)
                .Parameter("search", "query", static schema => schema.Type("string"))
                .RequestBody("application/json", static schema => schema.Type("object")
                    .Property("title", static property => property.Type("string"), required: true), required: true);
            configure?.Invoke(operation);
        }));

    private static void DefinePlaceInfo(SchemaBuilder schema) => _ = schema.Type("object")
        .Property("directory", property => property.Type("string"), required: true);

    private static void DefineLocationEnvelope(SchemaBuilder schema, Action<SchemaBuilder> data) => _ = schema
        .Type("object").AdditionalPropertiesFalse()
        .Property("location", property => property.Ref("PlaceInfo"), required: true)
        .Property("data", data, required: true);

    private static BindingException RefuseWidgetItem(SpecDocument document) =>
        Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(document, Selection("widget.item"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)))));

    private static OperationPlan BindWidgetList(SpecDocument document, IReadOnlyList<string>? strings = null) =>
        BindWidgets(document, integers: ["limit"], strings: strings ?? ["cursor"])
            .Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();

    private static BindingException RefuseMergedGadget(SpecDocument document, IReadOnlyDictionary<string, GroupCuration> groups) =>
        Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(document,
            Selection("gadget.part", "gizmo.list"), Curation(groups)));

    private static BindingException RefuseGadgetPartNames(SpecDocument document) =>
        Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(document,
            Selection("gadget.part", "gadget.part.get"),
            Curation(Groups("gadget", ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID")))));

    private static SpecScenario WidgetPayloadConflictScenario(Action<SchemaBuilder> payload) => SpecScenario.Define(spec => spec
        .WithSchema("PlaceInfo", schema => schema.Type("object")
            .Property("directory", property => property.Type("string"), required: true))
        .WithSchema("WidgetsEnvelope", schema => schema.Type("object").AdditionalPropertiesFalse()
            .Property("location", property => property.Ref("PlaceInfo"), required: true)
            .Property("data", payload, required: true))
        .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
            .Response(200, "application/json", schema => schema.Ref("WidgetsEnvelope")))
        .WithOperation("widget.summary", path: "/api/widget-summary", configure: operation => operation
            .Response(200, "application/json", schema => schema.Ref("WidgetsEnvelope"))));

    private static OperationPlan BindGadgetPart(SpecDocument document) => new BindingTestHost().Bind(
            document,
            Selection("gadget.part"),
            Curation(Groups("gadget", ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID"))))
        .Clients.Single(static client => client.Role == ClientRole.Handle).Operations.Single();

    private static async Task AssertFlatQueryAsync(OperationPlan list)
    {
        await Assert.That(list.QueryRequest!.DerivesFromListRequest).IsFalse();
        await Assert.That(list.QueryRequest.Properties).All().Satisfy(property => property.IsInherited, value => value.IsFalse());
    }

    private static async Task AssertFlatQueryNamesAsync(OperationPlan list, IReadOnlyList<string> names)
    {
        await Assert.That(list.QueryRequest!.DerivesFromListRequest).IsFalse();
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.PropertyName))
            .IsEquivalentTo(names, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    private static async Task AssertPromotedListDataAsync(EmitPlan plan)
    {
        await Assert.That(plan.Models).Contains(static model => string.Equals(model.Name, "WidgetListData", StringComparison.Ordinal));
        // The wrapper component's own spelling never reaches the surface.
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name.Contains("Response", StringComparison.Ordinal));
    }

    private static async Task AssertWidgetPayloadConflictAsync(SpecDocument document)
    {
        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("widget.list", "widget.summary"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)))));
        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
            && error.Problem.Contains("claimed as both 'WidgetListData' and 'WidgetSummaryData'", StringComparison.Ordinal));
    }

    private static async Task<OperationPlan> AssertCreateRequestAsync(EmitPlan plan)
    {
        var create = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(create.RequestBody!.TypeName).IsEqualTo("WidgetCreateRequest");
        return create;
    }

    private static async Task AssertProblemAsync(BindingException exception, BindingErrorCategory category, string problem) =>
        await Assert.That(exception.Errors).Contains(error => error.Category == category
            && error.Problem.Contains(problem, StringComparison.Ordinal));

    private static async Task AssertOperationProblemAsync(BindingException exception, string operationId, string expectedProblem) =>
        await Assert.That(exception.Errors).Contains(error => error.Category == BindingErrorCategory.Operation
            && string.Equals(error.Subject, operationId, StringComparison.Ordinal)
            && error.Problem.Contains(expectedProblem, StringComparison.Ordinal));

    [Test]
    [Arguments("vcs.changes.list", "none", "items", "operation 'vcs.changes.list' is a list whose last subject word 'Changes' ends in 's', which may already be plural; an operationNames row must name it (ADR-0008)")]
    [Arguments("addresses.list", "none", "items", "operation 'addresses.list' is a list whose last subject word 'Addresses' ends in 's', which may already be plural; an operationNames row must name it (ADR-0008)")]
    [Arguments("credential.settings.list", "method", "data", "operation 'credential.settings.list' is a list whose last subject word 'Settings' ends in 's', which may already be plural; an envelopePayloadNames row must name its payload member (ADR-0008)")]
    public async Task Bind_Should_Refuse_An_Ambiguous_List_Subject_Without_Its_Row(string operationId, string rows, string wireName, string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var document = await ListDocumentAsync(operationId, wireName);
        var curation = ListCuration(operationId, rows);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection(operationId), curation));

        await Assert.That(exception.Errors.Where(error => error.Category == BindingErrorCategory.Operation && error.Subject == operationId))
            .IsEquivalentTo([new BindingError(BindingErrorCategory.Operation, operationId, problem)], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("vcs.changes.list", "both", "data", "ListChangesAsync", "ListChanges", "Changes")]
    [Arguments("vcs.changes.list", "method", "items", "ListChangesAsync", "ListChanges", "Items")]
    [Arguments("boss.list", "none", "data", "ListBossesAsync", "ListBosses", "Bosses")]
    public async Task Bind_Should_Name_A_List_From_Its_Rows_Or_By_The_Rule(string operationId, string rows, string wireName,
        string methodName, string routeMemberName, string payloadName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var document = await ListDocumentAsync(operationId, wireName);

        var plan = new BindingTestHost().Bind(document, Selection(operationId), ListCuration(operationId, rows));

        var bound = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(bound.MethodName).IsEqualTo(methodName);
        await Assert.That(bound.RouteMemberName).IsEqualTo(routeMemberName);
        await Assert.That(bound.Envelope!.PayloadName).IsEqualTo(payloadName);
    }

    /// <summary>A handle list with no subject is <c>ListAsync</c>: it names no plural, so it needs no row.</summary>
    [Test]
    public async Task Bind_Should_Not_Require_A_Row_For_A_Handle_List_With_No_Subject()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("alias.list", path: "/api/alias/{aliasID}/items", configure: static operation => operation
                .Parameter("aliasID", "path", static schema => schema.Type("string"), required: true)
                .Response(200, "application/json", static schema => schema.Type("object")
                    .Property("items", static property => property.Type("array").Items(static item => item.Type("string")), required: true)))));

        var plan = new BindingTestHost().Bind(document, Selection("alias.list"),
            Curation(Groups("alias", ClientGroup(clientName: "Aliases", handleName: "AliasClient", handleParameter: "aliasID"))));

        var bound = plan.Clients.Single(static client => client.Role == ClientRole.Handle).Operations.Single();
        await Assert.That(bound.MethodName).IsEqualTo("ListAsync");
    }

    [Test]
    [Arguments("member", "GadgetClient", "multiple members map to C# name 'GetPartAsync'")]
    [Arguments("response", "gadget.part.get", "multiple operations map to response type 'GadgetPartResponse'", "multiple operations map to route member 'Gadgets.GetPart'")]
    [Arguments("route", "gadget.part.get", "multiple operations map to response type 'GadgetPartResponse'", "multiple operations map to route member 'Gadgets.GetPart'")]
    [Arguments("client", "GadgetClient", "client type name 'GadgetClient' collides with another generated type")]
    [Arguments("model", "Widget", "model type name 'ListCursor' collides with the hand-written spine type 'ListCursor'")]
    [Arguments("request", "widget.list", "request type name 'WidgetListRequest' collides with another generated type")]
    [Arguments("stream", "example.events", "multiple operations map to stream adapter 'ExampleEventsResponseStreamAdapter'")]
    public async Task Bind_Should_Refuse_Generated_Name_Collisions(string fault, string subject, string problem, string? secondProblem = null)
    {
        var curation = Curation(Groups("gadget", ClientGroup(fault == "client" ? "Gadget" : "Gadgets", "GadgetClient", "gadgetID")));
        var scenario = GadgetScenario();
        string[] selected = ["gadget.part"];
        if (fault is "member" or "response" or "route")
        {
            scenario = GadgetScenario(static spec => spec.WithOperation("gadget.part.get", path: "/api/gadget/{gadgetID}/part-copy/{partID}",
                configure: static operation => operation.Parameter("gadgetID", "path", static schema => schema.Type("string"), required: true)
                    .Parameter("partID", "path", static schema => schema.Type("string"), required: true)
                    .Response(200, "application/json", static schema => schema.Ref("GadgetPart"))));
            selected = ["gadget.part", "gadget.part.get"];
        }
        else if (fault is "model" or "request")
        {
            scenario = SpecScenario.Define(static spec => spec.WithSchema("Widget", static schema => schema.Type("object")
                    .Property("id", static property => property.Type("string"), required: true))
                .WithOperation("widget.list", configure: static operation => operation.Parameter("count", "query", static schema => schema.Type("string").Enum("true", "false"))
                    .Response(200, "application/json", static schema => schema.Ref("Widget"))));
            selected = ["widget.list"];
            curation = Curation(Groups("widget", RootGroup()), schemaNames: [SchemaName("Widget", fault == "model" ? "ListCursor" : "WidgetListRequest")]);
        }
        else if (fault == "stream")
        {
            scenario = new StreamOperationScenario();
            selected = ["example.events"];
            curation = Curation(Groups("example", RootGroup()), schemaNames: [SchemaName("ExampleEvent", "ExampleEventsResponseStreamAdapter")]);
        }
        var exception = await BindRefusalsAsync(scenario, curation, selected);
        BindingError[] expected = secondProblem is null ? [new(BindingErrorCategory.Naming, subject, problem)]
            : [new(BindingErrorCategory.Naming, subject, problem), new(BindingErrorCategory.Naming, subject, secondProblem)];
        await Assert.That(exception.Errors.Where(error => error.Category == BindingErrorCategory.Naming && error.Subject == subject))
            .IsEquivalentTo(expected, CollectionOrdering.Matching)
            .Because(string.Join(" | ", exception.Errors.Select(static error => error.Subject + ": " + error.Problem)));
    }

    /// <summary>
    /// Each row lands a curated name on a member the client emitter synthesizes. <c>fields</c> names two families
    /// whose properties differ but whose backing fields are both <c>_fooBar</c>; <c>Foo</c> beside <c>FOO</c>
    /// would refuse earlier, at the case-insensitive family prefix.
    /// </summary>
    [Test]
    [Arguments("Dispose")]
    [Arguments("Pipeline")]
    [Arguments("EmptyWidgetCreateRequest")]
    [Arguments("OpenCodeClient")]
    [Arguments("handle")]
    [Arguments("handle-field")]
    [Arguments("fields")]
    public async Task Bind_Should_Refuse_A_Client_Member_On_A_Synthesized_Name(string fault)
    {
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["gadget"] = fault switch
            {
                "handle" => ClientGroup("Gadgets", "GadgetId", "gadgetID"),
                "handle-field" => ClientGroup("Gadgets", "_gadgetId", "gadgetID"),
                "fields" => ClientGroup("Foo_Bar", null, null),
                _ => ClientGroup(fault, null, null),
            },
            ["widget"] = fault == "fields" ? ClientGroup("FooBar", null, null) : RootGroup(),
        };
        BindingError[] expected = fault switch
        {
            "Pipeline" => [ClientMemberRefusal(RootClient, "Pipeline"), ClientFieldRefusal(RootClient, "_pipeline")],
            "OpenCodeClient" => [EnclosingTypeMemberRefusal(RootClient, "OpenCodeClient")],
            "handle" => [EnclosingTypeMemberRefusal("GadgetId", "GadgetId")],
            "handle-field" => [EnclosingTypeMemberRefusal("_gadgetId", "_gadgetId")],
            "fields" => [ClientFieldRefusal(RootClient, "_fooBar")],
            _ => [ClientMemberRefusal(RootClient, fault)],
        };

        var exception = await BindRefusalsAsync(GadgetScenario(static spec => spec.WithOperation("widget.create", method: "post",
            path: "/api/widget", configure: static operation => operation
                .RequestBody("application/json", static body => body.Type("object")
                    .Property("title", static property => property.Type("string")), required: true)
                .Response(200, "application/json", static schema => schema.Ref("GadgetPart")))), Curation(groups), "gadget.part", "widget.create");

        await Assert.That(exception.Errors).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    /// <summary>Route containers nest in <c>OpenCodeRoutes</c> beside its <c>ToWireValue</c> converters, and hold their route members.</summary>
    [Test]
    [Arguments("OpenCodeRoutes", null, "route container 'OpenCodeRoutes' collides with a name the routes root declares")]
    [Arguments("ToWireValue", null, "route container 'ToWireValue' collides with a name the routes root declares")]
    [Arguments("Widgets", "WidgetsAsync", "route container 'Widgets' collides with its own route member 'Widgets'")]
    [Arguments("Widgets", "ToWireValueAsync", "route member 'ToWireValue' collides with a name the routes root declares")]
    public async Task Bind_Should_Refuse_A_Route_Name_On_A_Routes_Member(string container, string? methodName, string problem)
    {
        var curation = Curation(Groups("widget", ClientGroup(container, null, null)),
            operationNames: methodName is null ? [] : [OperationName("widget.list", methodName)]);

        var exception = await BindRefusalsAsync(WidgetListScenario(static operation => operation
            .Parameter("count", "query", static schema => schema.Type("string").Enum("alpha", "beta"))), curation, "widget.list");

        await Assert.That(exception.Errors).IsEquivalentTo([new BindingError(BindingErrorCategory.Naming, "widget.list", problem)], CollectionOrdering.Matching);
    }

    /// <summary>Builds a naming refusal for two generated client members sharing one C# name.</summary>
    private static BindingError ClientMemberRefusal(string client, string member) =>
        new(BindingErrorCategory.Naming, client, $"multiple members map to C# name '{member}'");

    private static BindingError ClientFieldRefusal(string client, string field) =>
        new(BindingErrorCategory.Naming, client, $"multiple fields map to C# name '{field}'");

    private static BindingError EnclosingTypeMemberRefusal(string type, string member) =>
        new(BindingErrorCategory.Naming, type, $"member '{member}' has the name of its enclosing type");

    [Test]
    [Arguments("reason", "envelope payload name curation must declare a reason")]
    [Arguments("duplicate", "envelope payload name curation is duplicated")]
    [Arguments("redundant", "envelopePayloadNames row for 'widget.entry.get' repeats the payload naming rule's derived value 'Entry'")]
    [Arguments("singleKey", "envelopePayloadNames row for 'widget.entry.get' repeats the payload naming rule's derived value 'Result'")]
    [Arguments("invalid", "payload name 'bad-name' is not a valid C# identifier", BindingErrorCategory.Naming)]
    [Arguments("invalid-binder", "payload name 'bad-name' is not a valid C# identifier", BindingErrorCategory.Naming)]
    [Arguments("spine", "payload name 'WidgetEntryResponse' collides with the response spine of 'WidgetEntryResponse'", BindingErrorCategory.Naming)]
    public async Task Bind_Should_Refuse_Invalid_Envelope_Payload_Rows(string variation, string problem, BindingErrorCategory category = BindingErrorCategory.Curation)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("widget.entry.get", configure: operation => operation
                .Response(200, "application/json", schema =>
                {
                    if (variation == "singleKey")
                    {
                        schema.Type("object").Property("result", static property => property.Type("string"), required: true);
                    }
                    else
                    {
                        schema.Type("string");
                    }
                }))));
        var row = EnvelopePayloadName("widget.entry.get", variation switch { "redundant" => "Entry", "singleKey" => "Result", "invalid" or "invalid-binder" => "bad-name", "spine" => "WidgetEntryResponse", _ => "Value" },
            reason: variation == "reason" ? " " : "Name the result.");
        var curation = Curation(Groups("widget", ClientGroup("Widgets", handleName: null, handleParameter: null)),
            envelopePayloadNames: variation == "duplicate" ? [row, row] : [row]);

        var errors = new BindingErrorCollector();
        var exception = Assert.Throws<BindingException>(() =>
        {
            if (variation == "invalid-binder")
            {
                var reachable = ReachableSchemaCollector.Collect(document, document.Operations, errors);
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                var context = SchemaBindingContext.Create(document, reachable, names, errors);
                var schemas = new SchemaPlanBinder(context).Bind(curation);
                _ = OperationPlanBinder.Bind(document, document.Operations, curation, schemas, errors);
                errors.ThrowIfAny();
            }
            else if (variation == "invalid")
            {
                CurationValidator.Validate(new BindingContext(document,
                    ReachableSchemaCollector.Collect(document, document.Operations, errors), curation, errors), document.Operations);
                errors.ThrowIfAny();
            }
            else
            {
                _ = new BindingTestHost().Bind(document, Selection("widget.entry.get"), curation);
            }
        });

        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(category, "widget.entry.get", problem));
    }

    private static readonly string[] ExpectedClientNames =
    [
        "OpenCodeClient",
        "AgentsClient",
        "CommandsClient",
        "ConfigClient",
        "CredentialsClient",
        "DebugClient",
        "EventsClient",
        "ExperimentalClient",
        "FileSystemClient",
        "FormsClient",
        "IntegrationClient",
        "IntegrationsClient",
        "LanguageModelsClient",
        "McpServersClient",
        "PermissionsClient",
        "PersistentPtyRawClient",
        "PersistentPtysRawClient",
        "PluginsClient",
        "ProjectsClient",
        "ProvidersClient",
        "PtyRawClient",
        "PtysRawClient",
        "ReferencesClient",
        "RpcClient",
        "ServerClient",
        "SessionClient",
        "SessionsClient",
        "ShellClient",
        "ShellsClient",
        "SkillsClient",
        "VcsClient",
        "WebsearchClient",
        "WorktreesClient",
    ];

    private static readonly string[] ExpectedSubClientPropertyNames =
    [
        "Agents",
        "Commands",
        "Config",
        "Credentials",
        "Debug",
        "Events",
        "Experimental",
        "FileSystem",
        "Forms",
        "Integrations",
        "LanguageModels",
        "McpServers",
        "Permissions",
        "PersistentPtys",
        "Plugins",
        "Projects",
        "Providers",
        "Ptys",
        "References",
        "Rpc",
        "Server",
        "Sessions",
        "Shells",
        "Skills",
        "Vcs",
        "Websearch",
        "Worktrees",
    ];

    private static readonly string[] ExpectedSubClientTypeNames =
    [
        "AgentsClient",
        "CommandsClient",
        "ConfigClient",
        "CredentialsClient",
        "DebugClient",
        "EventsClient",
        "ExperimentalClient",
        "FileSystemClient",
        "FormsClient",
        "IntegrationsClient",
        "LanguageModelsClient",
        "McpServersClient",
        "PermissionsClient",
        "PersistentPtysClient",
        "PluginsClient",
        "ProjectsClient",
        "ProvidersClient",
        "PtysClient",
        "ReferencesClient",
        "RpcClient",
        "ServerClient",
        "SessionsClient",
        "ShellsClient",
        "SkillsClient",
        "VcsClient",
        "WebsearchClient",
        "WorktreesClient",
    ];

    [Test]
    public async Task Bind_Should_Create_The_Selected_Pinned_Root_Client_Plan()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        await Assert.That(plan.Clients.Select(static client => client.Name))
            .IsEquivalentTo(ExpectedClientNames, StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert
            .That(plan.Clients)
            .All(static client => client.Namespace == (client.Emission is EmissionMode.InternalRaw ? "OpenCode.Sdk.Internal" : "OpenCode.Sdk"));

        var root = plan.Clients.Single(static client => client.Role == ClientRole.Root);
        await Assert.That(root.Name).IsEqualTo("OpenCodeClient");
        await Assert.That(root.SubClients.Select(static subClient => subClient.PropertyName))
            .IsEquivalentTo(ExpectedSubClientPropertyNames, StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(root.SubClients.Select(static subClient => subClient.TypeName))
            .IsEquivalentTo(ExpectedSubClientTypeNames, StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(root.HandleFactory).IsNull();
        await Assert.That(root.HandleParameter).IsNull();

        var health = plan.Clients.Single(static client => client.Name == "ServerClient")
            .Operations.Single(static operation => operation.MethodName == "GetInfoAsync");
        await Assert.That(health.OperationId).IsEqualTo("server.info");
        await Assert.That(health.MethodName).IsEqualTo("GetInfoAsync");
        await Assert.That(health.HttpMethod).IsEqualTo("get");
        await Assert.That(health.RouteTemplate).IsEqualTo("/api/info");
        await Assert.That(health.RouteContainerName).IsEqualTo("Server");
        await Assert.That(health.RouteMemberName).IsEqualTo("GetInfo");
        await Assert.That(health.Parameters).IsEmpty();
        await Assert.That(health.Summary).IsEqualTo("Get server info");
        await Assert.That(health.Description).IsNotNull();
        await Assert.That(health.Envelope!.ResponseTypeName).IsEqualTo("ServerInfoResponse");
        await Assert.That(health.Envelope.AdapterTypeName).IsEqualTo("ServerInfoResponseAdapter");
        await Assert.That(health.Envelope.PayloadName).IsEqualTo("ServerInfo");
        await Assert.That(health.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)health.Envelope.PayloadType!).Name).IsEqualTo("ServerInfo");
        await Assert.That(health.Envelope.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(health.ErrorMap.Statuses.Select(static status => status.StatusCode))
            .IsEquivalentTo([400, 401], CollectionOrdering.Matching);
        await Assert.That(health.ErrorMap.Statuses[0].Tags.Single().Tag).IsEqualTo("InvalidRequestError");
        await Assert.That(health.ErrorMap.Statuses[0].Tags.Single().TypeName).IsEqualTo("InvalidRequestError");
        await Assert.That(health.ErrorMap.Statuses[1].Tags.Single().Tag).IsEqualTo("UnauthorizedError");

        var location = root.Operations.Single(static operation => operation.MethodName == "GetLocationAsync");
        await Assert.That(location.HttpMethod).IsEqualTo("get");
        await Assert.That(location.RouteTemplate).IsEqualTo("/api/location");
        await Assert.That(location.RouteContainerName).IsEqualTo("Location");
        await Assert.That(location.RouteMemberName).IsEqualTo("Get");
        await Assert.That(location.Parameters).IsEmpty();
        await Assert.That(location.Envelope!.ResponseTypeName).IsEqualTo("LocationResponse");
        await Assert.That(location.Envelope.AdapterTypeName).IsEqualTo("LocationResponseAdapter");
        await Assert.That(location.Envelope.PayloadName).IsEqualTo("ResolvedLocation");
        await Assert.That(location.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)location.Envelope.PayloadType!).Name).IsEqualTo("LocationPublicInfo");
        await Assert.That(location.Envelope.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(location.ErrorMap.Statuses.Select(static status => status.StatusCode))
            .IsEquivalentTo([400, 401, 404], CollectionOrdering.Matching);
        await Assert.That(location.ErrorMap.Statuses[0].Tags.Single().Tag).IsEqualTo("InvalidRequestError");
        await Assert.That(location.ErrorMap.Statuses[1].Tags.Single().Tag).IsEqualTo("UnauthorizedError");
        await Assert.That(location.ErrorMap.Statuses[2].Tags.Single().Tag).IsEqualTo("LocationNotFoundError");

        await Assert.That(root.Operations.Count).IsEqualTo(2);
        await Assert.That(root.ContainerName).IsNull();
    }

    /// <summary>
    /// The root client's second pinned operation: location.reload rides beside location.get through
    /// its reason-bearing naming row, with the declared 503 arm the reload can answer.
    /// </summary>
    [Test]
    public async Task Bind_Should_Place_The_Location_Reload_On_The_Root_Client()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var root = plan.Clients.Single(static client => client.Role == ClientRole.Root);
        var reload = root.Operations.Single(static operation => operation.MethodName == "ReloadLocationsAsync");
        await Assert.That(reload.HttpMethod).IsEqualTo("post");
        await Assert.That(reload.RouteTemplate).IsEqualTo("/api/location/reload");
        await Assert.That(reload.RouteContainerName).IsEqualTo("Location");
        await Assert.That(reload.RouteMemberName).IsEqualTo("ReloadLocations");
        await Assert.That(reload.Parameters).IsEmpty();
        await Assert.That(reload.Envelope!.ResponseTypeName).IsEqualTo("LocationReloadResponse");
        await Assert.That(reload.ErrorMap.Statuses.Select(static status => status.StatusCode))
            .IsEquivalentTo([400, 401, 503], CollectionOrdering.Matching);
        await Assert.That(reload.ErrorMap.Statuses[2].Tags.Single().Tag).IsEqualTo("ServiceUnavailableError");
    }

    /// <summary>
    /// A plan carries the operation identity the document was ingested under, so a repaired
    /// identity reaches the plan in its intended form rather than the leaked group-qualified one.
    /// </summary>
    [Test]
    public async Task Bind_Should_Carry_The_Repaired_Operation_Identity_Onto_The_Plan()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var create = plan.Clients.Single(static client => client.Name == "PersistentPtysRawClient")
            .Operations.Single(static operation => operation.MethodName == "CreatePersistentPtyAsync");
        await Assert.That(create.OperationId).IsEqualTo("persistentPty.create");
        await Assert.That(create.RouteTemplate).IsEqualTo("/api/experimental/session/{sessionID}/terminal");
    }

    /// <summary>
    /// Both WebSocket connect operations (ADR-0021's hand-written session doors) are pinned by
    /// fingerprint rather than selected, ordinal-sorted; a spec refresh or a curation edit that
    /// drops either row, or reorders them, must fail this test rather than drift silently.
    /// </summary>
    [Test]
    public async Task Bind_Should_Report_Both_WebSocket_Operations_As_Transport_Owned()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        await Assert.That(plan.TransportOwnedOperationIds)
            .IsEquivalentTo(["persistentPty.connect", "pty.connect"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The committed curation against the pinned spec: every operation outside the transport-owned
    /// pair and the deferred client MCP pair binds; nothing remains pending.
    /// </summary>
    [Test]
    public async Task Bind_Should_Decline_Only_The_Client_Mcp_Pair_At_The_Pin()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        await Assert.That(plan.DeclinedOperations.Select(static operation => operation.OperationId))
            .IsEquivalentTo(["experimental.mcp.client", "experimental.mcp.client.token"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.PendingOperations).IsEmpty();
    }

    /// <summary>
    /// A raw-byte body is the caller's stream: it binds beside a standalone query record, whatever
    /// the query carries, because the query cannot ride a body that is not a model.
    /// </summary>
    [Test]
    public async Task Bind_Should_Bind_An_Octet_Stream_Body_As_A_Caller_Stream_Beside_Its_Query_Record()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("BlobWritten", schema => schema
                .Type("object")
                .Property("path", property => property.Type("string"), required: true))
            .WithOperation("blob.create", method: "post", path: "/api/blob", configure: operation => operation
                .Parameter("location", "query", QueryScenarioData.NullableLocationSelector, deepObject: true)
                .Parameter("path", "query", schema => schema.Type("string"), required: true)
                .RequestBody("application/octet-stream", schema => schema.Type("string").Format("binary"), required: true)
                .Response(200, "application/json", schema => schema.Ref("BlobWritten")))));

        var operation = new BindingTestHost()
            .Bind(document, Selection("blob.create"), Curation(Groups("blob", RootGroup()), stringQueryParameters: [StringQueryParameter("blob.create", "path")]))
            .Clients.SelectMany(static client => client.Operations)
            .Single();

        await Assert.That(operation.RequestBody!.IsBinary).IsTrue();
        await Assert.That(operation.RequestBody.TypeName).IsEqualTo("Stream");
        await Assert.That(operation.RequestBody.ParameterName).IsEqualTo("content");
        await Assert.That(operation.QueryRequest!.RidesRequestBody).IsFalse();
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert
            .That(operation.QueryRequest.Properties.Select(static property => $"{property.PropertyName}:{property.Kind}:{property.IsRequired}"))
            .IsEquivalentTo(["Location:Location:False", "Path:Text:True"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Optional_Octet_Stream_Body()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("BlobWritten", schema => schema
                .Type("object")
                .Property("path", property => property.Type("string"), required: true))
            .WithOperation("health.write", method: "post", path: "/api/health/write", configure: operation => operation
                .RequestBody("application/octet-stream", schema => schema.Type("string").Format("binary"))
                .Response(200, "application/json", schema => schema.Ref("BlobWritten")))));

        await AssertOperationRefusalAsync(document, "health.write", "the request body must be declared required");
    }

    [Test]
    public async Task Bind_Should_Create_The_Selected_Pinned_Session_Client_Plans()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var sessions = plan.Clients.Single(static client => client.Name == "SessionsClient");
        await Assert.That(sessions.ContainerName).IsEqualTo("Sessions");
        await Assert.That(sessions.Operations.Select(static operation => operation.MethodName))
            .IsEquivalentTo(["CreateSessionAsync", "GetActiveAsync", "ListSessionsAsync"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(sessions.HandleFactory!.MethodName).IsEqualTo("GetSessionClient");
        await Assert.That(sessions.HandleFactory.HandleTypeName).IsEqualTo("SessionClient");
        await Assert.That(sessions.HandleFactory.Parameter.WireName).IsEqualTo("sessionID");
        await Assert.That(sessions.HandleFactory.Parameter.Name).IsEqualTo("sessionId");
        await Assert.That(sessions.HandleFactory.Parameter.TypeName).IsEqualTo("string");

        var list = sessions.Operations.Single(static operation => operation.MethodName == "ListSessionsAsync");
        await Assert.That(list.QueryRequest!.TypeName).IsEqualTo("SessionListRequest");
        await Assert.That(list.QueryRequest.DerivesFromListRequest).IsTrue();
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.PropertyName))
            .IsEquivalentTo(["Limit", "Order", "Search", "ParentId", "Archived", "Directory", "Project", "Subpath", "Cursor"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(list.QueryRequest.Properties.Where(static property => property.IsInherited).Select(static property => property.PropertyName))
            .IsEquivalentTo(["Limit", "Order", "Cursor"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.CursorList);
        await Assert.That(list.Envelope.PayloadName).IsEqualTo("Sessions");
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(((ListTypeReferencePlan)list.Envelope.PayloadType!).ElementType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((ListTypeReferencePlan)list.Envelope.PayloadType).ElementType).Name)
            .IsEqualTo("SessionInfo");
        await Assert.That(list.ErrorMap.Statuses[0].Tags.Select(static tag => tag.TypeName))
            .IsEquivalentTo(["InvalidCursorError", "InvalidRequestError"], StringComparer.Ordinal, CollectionOrdering.Matching);

        var create = sessions.Operations.Single(static operation => operation.MethodName == "CreateSessionAsync");
        await Assert.That(create.HttpMethod).IsEqualTo("post");
        await Assert.That(create.RequestBody!.TypeName).IsEqualTo("SessionCreateRequest");
        await Assert.That(create.RequestBody.IsOptional).IsTrue();
        await Assert.That(create.Envelope!.ResponseTypeName).IsEqualTo("SessionCreateResponse");
        await Assert.That(create.Envelope.PayloadName).IsEqualTo("Session");
    }

    [Test]
    public async Task Bind_Should_Create_The_Selected_Pinned_Shell_Plans()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var shells = plan.Clients.Single(static client => client.Name == "ShellsClient");
        await Assert.That(shells.ContainerName).IsEqualTo("Shells");
        await Assert.That(shells.Operations.Select(static operation => operation.MethodName))
            .IsEquivalentTo(["CreateShellAsync", "ListShellsAsync"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(shells.HandleFactory!.MethodName).IsEqualTo("GetShellClient");
        await Assert.That(shells.HandleFactory.Parameter.WireName).IsEqualTo("id");

        var createShell = shells.Operations.Single(static operation => operation.MethodName == "CreateShellAsync");
        await Assert.That(createShell.RequestBody!.TypeName).IsEqualTo("ShellCreateRequest");
        await Assert.That(createShell.QueryRequest!.RidesRequestBody).IsTrue();
        await Assert.That(createShell.QueryRequest.TypeName).IsEqualTo("ShellCreateRequest");
        await Assert.That(createShell.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocation);
        await Assert.That(createShell.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)createShell.Envelope.PayloadType!).Name).IsEqualTo("ShellInfo");
        await Assert.That(createShell.Envelope.LocationTypeName).IsEqualTo("LocationPublicRef");

        var listShells = shells.Operations.Single(static operation => operation.MethodName == "ListShellsAsync");
        await Assert.That(listShells.QueryRequest!.TypeName).IsEqualTo("ShellListRequest");
        await Assert.That(listShells.QueryRequest.RidesRequestBody).IsFalse();
        await Assert.That(listShells.QueryRequest.Properties.Single().Kind).IsEqualTo(QueryValueKind.Location);
        await Assert.That(listShells.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocationList);
    }

    /// <summary>
    /// The pinned session handle's names in binder order: grammar verbs mechanically, the
    /// handle's own read/update/remove without a restated family, and every non-grammar
    /// operation through its reason-bearing row — no HTTP verb anywhere (ADR-0008).
    /// </summary>
    private static readonly string[] ExpectedSessionClientMethodNames =
    [
        "AddSyntheticMessageAsync",
        "CancelFormAsync",
        "CancelInboxAsync",
        "ClearRevertAsync",
        "CommitRevertAsync",
        "CompactAsync",
        "CreateFormAsync",
        "CreatePermissionAsync",
        "ForkAsync",
        "GenerateTextAsync",
        "GetAsync",
        "GetContextAsync",
        "GetDiffAsync",
        "GetFormAsync",
        "GetMessageAsync",
        "GetPermissionAsync",
        "InterruptAsync",
        "ListFormsAsync",
        "ListInboxAsync",
        "ListMessagesAsync",
        "ListRequestsAsync",
        "MarkViewedAsync",
        "MoveAsync",
        "MoveToolsToBackgroundAsync",
        "PromptAsync",
        "ReadLogAsync",
        "RemoveAsync",
        "ReplyToFormAsync",
        "ReplyToPermissionAsync",
        "RunCommandAsync",
        "RunShellCommandAsync",
        "SetEnvironmentAsync",
        "StageRevertAsync",
        "SwitchAgentAsync",
        "SwitchModelAsync",
        "UpdateAsync",
        "UpdateInboxAsync",
    ];

    [Test]
    public async Task Bind_Should_Create_The_Selected_Pinned_Handle_Plans()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var session = plan.Clients.Single(static client => client.Name == "SessionClient");
        await Assert.That(session.ContainerName).IsEqualTo("Sessions");
        await Assert.That(session.HandleParameter!.WireName).IsEqualTo("sessionID");
        await Assert.That(session.HandleParameter.IsHandleParameter).IsTrue();
        await Assert.That(session.Operations.Select(static operation => operation.MethodName))
            .IsEquivalentTo(ExpectedSessionClientMethodNames, StringComparer.Ordinal, CollectionOrdering.Matching);

        var remove = session.Operations.Single(static operation => operation.MethodName == "RemoveAsync");
        await Assert.That(remove.HttpMethod).IsEqualTo("delete");
        await Assert.That(remove.Envelope!.Kind).IsEqualTo(EnvelopeKind.NoContent);
        await Assert.That(remove.Envelope.SuccessStatusCode).IsEqualTo(204);
        await Assert.That(remove.Envelope.ResponseTypeName).IsEqualTo("SessionRemoveResponse");

        var shell = plan.Clients.Single(static client => client.Name == "ShellClient");
        await Assert.That(shell.HandleParameter!.WireName).IsEqualTo("id");
        await Assert.That(shell.Operations.Select(static operation => operation.MethodName))
            .IsEquivalentTo(["GetAsync", "GetOutputAsync", "RemoveAsync"], StringComparer.Ordinal, CollectionOrdering.Matching);

        var getShell = shell.Operations.Single(static operation => operation.MethodName == "GetAsync");
        await Assert.That(getShell.QueryRequest!.TypeName).IsEqualTo("ShellRequest");
        await Assert.That(getShell.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocation);

        var messages = session.Operations.Single(static operation => operation.MethodName == "ListMessagesAsync");
        await Assert.That(messages.QueryRequest!.TypeName).IsEqualTo("SessionMessageListRequest");
        await Assert.That(messages.QueryRequest.DerivesFromListRequest).IsTrue();
        await Assert.That(messages.Envelope!.Kind).IsEqualTo(EnvelopeKind.CursorList);
        await Assert.That(messages.Envelope.PayloadName).IsEqualTo("Messages");
        await Assert.That(messages.ErrorMap.Statuses.Select(static status => status.StatusCode))
            .IsEquivalentTo([400, 401, 404, 500], CollectionOrdering.Matching);

        var message = session.Operations.Single(static operation => operation.MethodName == "GetMessageAsync");
        await Assert.That(message.RouteTemplate).IsEqualTo("/api/session/{sessionID}/message/{messageID}");
        await Assert.That(message.RouteContainerName).IsEqualTo("Sessions");
        await Assert.That(message.RouteMemberName).IsEqualTo("GetMessage");
        await Assert.That(message.Summary).IsEqualTo("Get session message");
        await Assert.That(message.Parameters.Select(static parameter => parameter.Name))
            .IsEquivalentTo(["sessionId", "messageId"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(message.Parameters[0].IsHandleParameter).IsTrue();
        await Assert.That(message.Parameters[1].IsHandleParameter).IsFalse();
        await Assert.That(message.Parameters[1].WireName).IsEqualTo("messageID");
        await Assert.That(message.Parameters[1].TypeName).IsEqualTo("string");
        await Assert.That(message.Envelope!.ResponseTypeName).IsEqualTo("SessionMessageResponse");
        await Assert.That(message.Envelope.AdapterTypeName).IsEqualTo("SessionMessageResponseAdapter");
        await Assert.That(message.Envelope.PayloadName).IsEqualTo("Message");
        await Assert.That(message.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)message.Envelope.PayloadType!).Name).IsEqualTo("ISessionMessageInfo");
        await Assert.That(message.Envelope.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(message.ErrorMap.Statuses.Select(static status => status.StatusCode))
            .IsEquivalentTo([400, 401, 404], CollectionOrdering.Matching);
        await Assert.That(message.ErrorMap.Statuses[2].Tags.Select(static tag => tag.Tag))
            .IsEquivalentTo(["MessageNotFoundError", "SessionNotFoundError"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(message.ErrorMap.Statuses[2].Tags).All(static tag => tag.Tag == tag.TypeName);
    }

    /// <summary>
    /// The pin's only location envelope whose 'data' is an inline object: it promotes into a
    /// model named from the operation and the envelope stays a single-value DataLocation.
    /// </summary>
    [Test]
    public async Task Bind_Should_Promote_The_Pinned_Shell_Output_Payload()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var output = plan
            .Clients.Single(static client => client.Name == "ShellClient")
            .Operations.Single(static operation => operation.MethodName == "GetOutputAsync");

        await Assert.That(output.QueryRequest!.TypeName).IsEqualTo("ShellOutputRequest");
        await Assert.That(output.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocation);
        await Assert.That(output.Envelope.PayloadName).IsEqualTo("Output");
        await Assert.That(output.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)output.Envelope.PayloadType!).Name).IsEqualTo("ShellOutputData");
        await Assert.That(output.Envelope.LocationTypeName).IsEqualTo("LocationPublicRef");
        await Assert.That(plan.Models).Contains(static model => string.Equals(model.Name, "ShellOutputData", StringComparison.Ordinal));
    }

    /// <summary>
    /// The pinned handoff's single-key success body flattens: the payload arrives under
    /// the body's own key, so the wrapper never becomes a model and the caller reads the value
    /// directly.
    /// </summary>
    [Test]
    public async Task Bind_Should_Flatten_The_Pinned_Single_Key_Success_Envelopes()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();

        var handoff = plan
            .Clients.Single(static client => client.Name == "PersistentPtysRawClient")
            .Operations.Single(static operation => operation.MethodName == "HandoffAsync");
        await Assert.That(handoff.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(handoff.Envelope.WireMemberName).IsEqualTo("handoff");
        await Assert.That(handoff.Envelope.PayloadName).IsEqualTo("Handoff");
        await Assert.That(((NamedTypeReferencePlan)handoff.Envelope.PayloadType!).Name).IsEqualTo("PersistentPtyHandoff");
        await Assert.That(handoff.Envelope.PayloadType.IsNullable).IsTrue();
        await Assert.That(plan.Models).DoesNotContain(static model => string.Equals(model.Name, "PersistentPtyHandoffData", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Recognize_Every_Pinned_ListRequest_Cursor_Operation_As_Paginated()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();
        var session = plan.Clients.Single(static client => client.Name == "SessionClient");
        var sessions = plan.Clients.Single(static client => client.Name == "SessionsClient");
        var messages = session.Operations.Single(static operation => operation.MethodName == "ListMessagesAsync");
        var pagination = messages.Pagination;

        await Assert.That(pagination).IsNotNull();
        await Assert.That(pagination.MethodName).IsEqualTo("EnumerateMessagesAsync");
        await Assert.That(pagination.RequestTypeName).IsEqualTo("SessionMessageListRequest");
        await Assert.That(pagination.PageTypeName).IsEqualTo("SessionMessageListResponse");
        await Assert.That(pagination.ItemTypeName).IsEqualTo("ISessionMessageInfo");
        await Assert.That(pagination.PayloadName).IsEqualTo("Messages");

        var sessionPagination = sessions.Operations
            .Single(static operation => operation.MethodName == "ListSessionsAsync")
            .Pagination;
        await Assert.That(sessionPagination).IsNotNull();
        await Assert.That(sessionPagination.MethodName).IsEqualTo("EnumerateSessionsAsync");
        await Assert.That(sessionPagination.RequestTypeName).IsEqualTo("SessionListRequest");
        await Assert.That(sessionPagination.PageTypeName).IsEqualTo("SessionListResponse");
        await Assert.That(sessionPagination.ItemTypeName).IsEqualTo("SessionInfo");
        await Assert.That(sessionPagination.PayloadName).IsEqualTo("Sessions");
    }

    [Test]
    public async Task Bind_Should_Derive_Selected_Query_Types_From_The_Pinned_OpenApi()
    {
        var plan = await new BindingTestHost().BindPinnedAsync();
        var sessions = plan.Clients.Single(static client => client.Name == "SessionsClient");
        var session = plan.Clients.Single(static client => client.Name == "SessionClient");

        var sessionList = sessions.Operations.Single(static operation => operation.MethodName == "ListSessionsAsync");
        await Assert
            .That(sessionList.QueryRequest!.Properties.Single(static property => property.WireName == "limit").Kind)
            .IsEqualTo(QueryValueKind.Int32);
        await Assert
            .That(sessionList.QueryRequest.Properties.Single(static property => property.WireName == "order").Kind)
            .IsEqualTo(QueryValueKind.ListOrder);

        var messageList = session.Operations.Single(static operation => operation.MethodName == "ListMessagesAsync");
        await Assert
            .That(messageList.QueryRequest!.Properties.Single(static property => property.WireName == "limit").Kind)
            .IsEqualTo(QueryValueKind.Int32);
        await Assert
            .That(messageList.QueryRequest.Properties.Single(static property => property.WireName == "order").Kind)
            .IsEqualTo(QueryValueKind.ListOrder);
        await Assert
            .That(messageList.QueryRequest.Properties.Single(static property => property.WireName == "cursor").Kind)
            .IsEqualTo(QueryValueKind.Text);

        var sessionLog = session.Operations.Single(static operation => operation.MethodName == "ReadLogAsync");
        await Assert
            .That(sessionLog.QueryRequest!.Properties.Single(static property => property.WireName == "after").Kind)
            .IsEqualTo(QueryValueKind.Int64);
        await Assert
            .That(sessionLog.QueryRequest.Properties.Single(static property => property.WireName == "follow").Kind)
            .IsEqualTo(QueryValueKind.BooleanText);
        await Assert
            .That(messageList.QueryRequest.Properties.Single(static property => property.WireName == "cursor").Description)
            .Contains("Do not combine with order");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Synthetic_Same_Shape_Group_Through_The_Same_Rules()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario());

        var plan = new BindingTestHost().Bind(
            document,
            Selection("gadget.part"),
            Curation(Groups("gadget", ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID"))));

        var root = plan.Clients.Single(static client => client.Role == ClientRole.Root);
        await Assert.That(root.SubClients.Single().PropertyName).IsEqualTo("Gadgets");
        await Assert.That(root.SubClients.Single().TypeName).IsEqualTo("GadgetsClient");

        var gadgets = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        await Assert.That(gadgets.HandleFactory!.MethodName).IsEqualTo("GetGadgetClient");
        await Assert.That(gadgets.HandleFactory.Parameter.Name).IsEqualTo("gadgetId");

        var gadget = plan.Clients.Single(static client => client.Role == ClientRole.Handle);
        await Assert.That(gadget.Name).IsEqualTo("GadgetClient");
        var part = gadget.Operations.Single();
        await Assert.That(part.MethodName).IsEqualTo("GetPartAsync");
        await Assert.That(part.RouteTemplate).IsEqualTo("/api/gadget/{gadgetID}/part/{partID}");
        await Assert.That(part.RouteContainerName).IsEqualTo("Gadgets");
        await Assert.That(part.RouteMemberName).IsEqualTo("GetPart");
        await Assert.That(part.Parameters.Select(static parameter => parameter.Name))
            .IsEquivalentTo(["gadgetId", "partId"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(part.Envelope!.ResponseTypeName).IsEqualTo("GadgetPartResponse");
        await Assert.That(part.Envelope.AdapterTypeName).IsEqualTo("GadgetPartResponseAdapter");
        await Assert.That(part.Envelope.PayloadName).IsEqualTo("Part");
        await Assert.That(part.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)part.Envelope.PayloadType!).Name).IsEqualTo("GadgetPart");
        await Assert.That(part.Envelope.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(part.ErrorMap.Statuses.Single().StatusCode).IsEqualTo(404);
        await Assert.That(part.ErrorMap.Statuses.Single().Tags.Single().TypeName).IsEqualTo("GadgetMissingError");
    }

    [Test]
    public async Task Bind_Should_Keep_A_Group_Without_Handle_Declaration_Flat()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.item", path: "/api/widget/{sessionID}/item", configure: operation => operation
                .Parameter("sessionID", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.item"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null))));

        await Assert.That(plan.Clients).DoesNotContain(static client => client.Role == ClientRole.Handle);
        var widgets = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        await Assert.That(widgets.HandleFactory).IsNull();
        var item = widgets.Operations.Single();
        await Assert.That(item.MethodName).IsEqualTo("GetItemAsync");
        await Assert.That(item.Parameters.Single().Name).IsEqualTo("sessionId");
        await Assert.That(item.Parameters.Single().IsHandleParameter).IsFalse();
    }

    [Test]
    public async Task Bind_Should_Keep_Collection_Operations_On_The_Collection_Client()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario(spec => spec
            .WithOperation("gadget.overview", path: "/api/gadget-overview", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("GadgetPart")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("gadget.part", "gadget.overview"),
            Curation(Groups("gadget", ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID"))));

        var gadgets = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        await Assert.That(gadgets.Operations.Single().MethodName).IsEqualTo("GetOverviewAsync");
        var gadget = plan.Clients.Single(static client => client.Role == ClientRole.Handle);
        await Assert.That(gadget.Operations.Single().MethodName).IsEqualTo("GetPartAsync");
    }

    [Test]
    public async Task Bind_Should_Order_Parameters_By_Route_Template_Position()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario(parametersReversed: true));

        var part = BindGadgetPart(document);
        await Assert.That(part.Parameters.Select(static parameter => parameter.WireName))
            .IsEquivalentTo(["gadgetID", "partID"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("ListCursor")]
    public async Task Bind_Should_Refuse_A_Model_Colliding_With_A_Spine_Type_Name(string schemaName)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema(schemaName, schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.item", path: "/api/widget/item", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref(schemaName)))));

        var exception = RefuseWidgetItem(document);

        await AssertProblemAsync(exception, BindingErrorCategory.Naming, "spine");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Bodyless_Post_Operation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.create", method: "post", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        var plan = BindWidgets(document, "widget.create");

        var create = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(create.HttpMethod).IsEqualTo("post");
        await Assert.That(create.RequestBody).IsNull();
        await Assert.That(create.Envelope).IsNotNull();
    }

    [Test]
    public async Task Bind_Should_Bind_A_Put_Operation_With_A_Request_Body()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.update", method: "put", path: "/api/widget", configure: operation => operation
                .RequestBody("application/json", schema => schema
                    .Type("object")
                    .Property("title", property => property.Type("string"), required: true), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        var plan = BindWidgets(document, "widget.update");

        var update = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(update.HttpMethod).IsEqualTo("put");
        await Assert.That(update.RequestBody).IsNotNull();
        await Assert.That(update.RequestBody!.TypeName).IsEqualTo("WidgetUpdateRequest");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Json_Request_Body_Into_A_Request_Model()
    {
        var document = await BindingTestHost.IngestAsync(WidgetCreateScenario(body => body
            .Type("object")
            .AdditionalPropertiesFalse()
            .Property("id", property => property.AnyOf(
                static branch => branch.Type("string"),
                static branch => branch.Type("null")))
            .Property("title", property => property.AnyOf(
                static branch => branch.Type("string"),
                static branch => branch.Type("null")))));

        var plan = BindWidgets(document, "widget.create");

        var create = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(create.HttpMethod).IsEqualTo("post");
        await Assert.That(create.RequestBody).IsNotNull();
        await Assert.That(create.RequestBody!.TypeName).IsEqualTo("WidgetCreateRequest");
        await Assert.That(create.RequestBody.ParameterName).IsEqualTo("request");
        await Assert.That(create.RequestBody.IsOptional).IsTrue();
        await Assert.That(plan.Models.Select(static model => model.Name)).Contains("WidgetCreateRequest");
        await Assert.That(plan.Registry.TypeNames).Contains("WidgetCreateRequest");
    }

    [Test]
    public async Task Bind_Should_Require_The_Request_Parameter_When_The_Body_Has_Required_Properties()
    {
        var document = await BindingTestHost.IngestAsync(WidgetCreateScenario(body => body
            .Type("object")
            .Property("title", property => property.Type("string"), required: true)));

        var plan = BindWidgets(document, "widget.create");

        var create = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(create.RequestBody!.IsOptional).IsFalse();
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Non_Json_Request_Body()
    {
        var document = await BindingTestHost.IngestAsync(WidgetCreateScenario(
            body => body.Type("object").Property("title", property => property.Type("string")),
            mediaType: "text/plain"));

        await AssertWidgetRefusalAsync(document, "JSON", "widget.create");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Non_Object_Request_Body()
    {
        var document = await BindingTestHost.IngestAsync(WidgetCreateScenario(body => body.Type("string")));

        await AssertWidgetRefusalAsync(document, "object schema", "widget.create");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Optional_Request_Body()
    {
        var document = await BindingTestHost.IngestAsync(WidgetCreateScenario(
            body => body.Type("object").Property("title", property => property.Type("string")),
            required: false));

        await AssertWidgetRefusalAsync(document, "declared required", "widget.create");
    }

    [Test]
    public async Task Bind_Should_Bind_Optional_Nullable_Query_Parameters_Beside_The_Inherited_Spine()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("limit", "query", QueryScenarioData.NullableString)
            .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)
            .Parameter("cursor", "query", QueryScenarioData.NullableString)
            .Parameter("search", "query", QueryScenarioData.NullableString)
            .Parameter("parentID", "query", QueryScenarioData.NullableParentFilter)));

        var plan = BindWidgets(document, integers: ["limit"], strings: ["cursor", "search"]);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.QueryRequest).IsNotNull();
        await Assert.That(list.QueryRequest!.TypeName).IsEqualTo("WidgetListRequest");
        await Assert.That(list.QueryRequest.DerivesFromListRequest).IsTrue();
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.WireName))
            .IsEquivalentTo(["limit", "order", "cursor", "search", "parentID"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.PropertyName))
            .IsEquivalentTo(["Limit", "Order", "Cursor", "Search", "ParentId"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.Kind))
            .IsEquivalentTo([
                    QueryValueKind.Int32,
                    QueryValueKind.ListOrder,
                    QueryValueKind.Text,
                    QueryValueKind.Text,
                    QueryValueKind.SessionParentFilter,
                ], CollectionOrdering.Matching);
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.IsInherited))
            .IsEquivalentTo([true, true, true, false, false], CollectionOrdering.Matching);
        await Assert.That(list.Parameters).IsEmpty();
    }

    [Test]
    public async Task Bind_Should_Keep_Query_Description_Without_Deriving_Validation()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("limit", "query", schema => schema
                .AnyOf(
                    branch => branch.Type("string"),
                    branch => branch.Type("null"))
                .Description("Must be a positive count."))));

        var plan = BindWidgets(document, strings: ["limit"]);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var property = list.QueryRequest!.Properties.Single();
        await Assert.That(property.Kind).IsEqualTo(QueryValueKind.Text);
        await Assert.That(property.Description).IsEqualTo("Must be a positive count.");
    }

    [Test]
    public async Task Bind_Should_Keep_Query_Parameter_Schemas_Out_Of_The_Model_Closure()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)
            .Parameter("parentID", "query", QueryScenarioData.NullableParentFilter)));

        var plan = BindWidgets(document);

        await Assert.That(plan.Models.Select(static model => model.Name))
            .IsEquivalentTo(["WidgetInfo"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Unions).IsEmpty();
        await Assert.That(plan.Registry.TypeNames).IsEquivalentTo(["WidgetInfo"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Derive_The_Query_Request_From_The_List_Request_Base_When_The_Query_Is_Only_The_Trio()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("limit", "query", QueryScenarioData.NullableString)
            .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)
            .Parameter("cursor", "query", QueryScenarioData.NullableString)));

        var list = BindWidgetList(document);
        await Assert.That(list.QueryRequest!.TypeName).IsEqualTo("WidgetListRequest");
        await Assert.That(list.QueryRequest.DerivesFromListRequest).IsTrue();
        await Assert.That(list.QueryRequest.Properties.Select(static property => property.PropertyName))
            .IsEquivalentTo(["Limit", "Order", "Cursor"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(list.QueryRequest.Properties).All().Satisfy(property => property.IsInherited, value => value.IsTrue());
    }

    [Test]
    public async Task Bind_Should_Keep_The_Query_Request_Flat_When_A_Spine_Parameter_Carries_An_Unadmitted_Schema()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("limit", "query", QueryScenarioData.NullableString)
            .Parameter("order", "query", schema => schema.AnyOf(
                branch => branch.Type("string").Enum("asc", "desc", "shuffled"),
                branch => branch.Type("null")))
            .Parameter("cursor", "query", QueryScenarioData.NullableString)));

        var list = BindWidgetList(document);
        await AssertFlatQueryAsync(list);
    }

    [Test]
    public async Task Bind_Should_Keep_The_Query_Request_Flat_When_A_Spine_Parameter_Is_Declared_Required()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("limit", "query", schema => schema.Type("string"), required: true)
            .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)
            .Parameter("cursor", "query", QueryScenarioData.NullableString)));

        var list = BindWidgetList(document);
        await AssertFlatQueryAsync(list);
    }

    [Test]
    public async Task Bind_Should_Keep_The_Query_Request_Flat_When_The_Trio_Is_Incomplete()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("limit", "query", QueryScenarioData.NullableString)
            .Parameter("cursor", "query", QueryScenarioData.NullableString)));

        var list = BindWidgetList(document);
        await AssertFlatQueryNamesAsync(list, ["Limit", "Cursor"]);
    }

    [Test]
    public async Task Bind_Should_Leave_The_Query_Request_Absent_When_An_Operation_Has_No_Query_Parameters()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario());

        var part = BindGadgetPart(document);
        await Assert.That(part.QueryRequest).IsNull();
    }

    [Test]
    public async Task Bind_Should_Bind_A_Cursor_List_Envelope()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario());

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.CursorList);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(((ListTypeReferencePlan)list.Envelope.PayloadType!).ElementType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((ListTypeReferencePlan)list.Envelope.PayloadType).ElementType).Name)
            .IsEqualTo("WidgetInfo");
        await Assert.That(list.Envelope.EnvelopeDtoTypeName).IsEqualTo("WidgetListResponseEnvelope");
        await Assert.That(plan.Models.Select(static model => model.Name))
            .IsEquivalentTo(["WidgetInfo"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Registry.TypeNames).IsEquivalentTo(["WidgetInfo", "WidgetListResponseEnvelope"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Admit_Cursor_Pagination_When_Filters_Ride_The_Spine()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: operation => operation
            .Parameter("limit", "query", QueryScenarioData.NullableString)
            .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)
            .Parameter("cursor", "query", QueryScenarioData.NullableString)
            .Parameter("search", "query", QueryScenarioData.NullableString)));

        var plan = BindWidgets(document, integers: ["limit"], strings: ["cursor", "search"]);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.QueryRequest!.DerivesFromListRequest).IsTrue();
        await Assert.That(list.Pagination).IsNotNull();
        await Assert.That(list.Pagination.MethodName).IsEqualTo("EnumerateWidgetsAsync");
        await Assert.That(list.Pagination.RequestTypeName).IsEqualTo("WidgetListRequest");
        await Assert.That(list.Pagination.PageTypeName).IsEqualTo("WidgetListResponse");
        await Assert.That(list.Pagination.ItemTypeName).IsEqualTo("WidgetInfo");
    }

    [Test]
    public async Task Bind_Should_Leave_Cursor_Pagination_Absent_When_The_Spine_Is_Incomplete()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: operation => operation
            .Parameter("limit", "query", QueryScenarioData.NullableString)
            .Parameter("cursor", "query", QueryScenarioData.NullableString)));

        var list = BindWidgetList(document);
        await Assert.That(list.QueryRequest!.DerivesFromListRequest).IsFalse();
        await Assert.That(list.Pagination).IsNull();
    }

    [Test]
    public async Task Bind_Should_Omit_Only_Cursor_Pagination_When_A_Filter_Riding_The_Spine_Is_Required()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: operation => CursorQuery(operation)
            .Parameter("search", "query", schema => schema.Type("string"), required: true)));

        var list = BindWidgetList(document, ["cursor", "search"]);
        await Assert.That(list.MethodName).IsEqualTo("ListWidgetsAsync");
        await Assert.That(list.QueryRequest!.Properties.Single(static property => property.WireName == "search").IsRequired).IsTrue();
        await Assert.That(list.QueryRequest.HasRequiredMember).IsTrue();
        await Assert.That(list.Pagination).IsNull();
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Cursor_Paginator_Whose_Enumeration_Name_Cannot_Be_Derived()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: operation => operation
            .Parameter("limit", "query", QueryScenarioData.NullableString)
            .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)
            .Parameter("cursor", "query", QueryScenarioData.NullableString)));
        var curation = Curation(
            Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)),
            operationNames: [OperationName("widget.list", "BrowseWidgetsAsync")],
            queryParameterTypes: [QueryParameterType("widget.list", "limit", "int")],
            stringQueryParameters: [StringQueryParameter("widget.list", "cursor")]);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("widget.list"),
            curation));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
                                                        && error.Problem.Contains(
                                                            "must be an asynchronous List method",
                                                            StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Bind_A_Data_Location_Envelope()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario());

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocation);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)list.Envelope.PayloadType!).Name).IsEqualTo("WidgetInfo");
        await Assert.That(list.Envelope.LocationTypeName).IsEqualTo("PlaceInfo");
        await Assert.That(list.Envelope.EnvelopeDtoTypeName).IsEqualTo("WidgetListResponseEnvelope");
        await Assert.That(plan.Models.Select(static model => model.Name))
            .IsEquivalentTo(["PlaceInfo", "WidgetInfo"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(plan.Registry.TypeNames).Contains("WidgetListResponseEnvelope", StringComparer.Ordinal);
    }

    [Test]
    public async Task Bind_Should_Bind_A_Data_Location_List_Envelope()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario(
            data: static property => property.Type("array").Items(static item => item.Ref("WidgetInfo"))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocationList);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(((ListTypeReferencePlan)list.Envelope.PayloadType!).ElementType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((ListTypeReferencePlan)list.Envelope.PayloadType).ElementType).Name)
            .IsEqualTo("WidgetInfo");
        await Assert.That(list.Envelope.LocationTypeName).IsEqualTo("PlaceInfo");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Data_Location_Envelope_With_An_Optional_Location()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario(locationRequired: false));

        await AssertWidgetRefusalAsync(document, "require exactly");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Data_Location_Sibling_Without_A_Named_Schema()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario(
            location: static property => property
                .Type("object")
                .Property("directory", static inner => inner.Type("string"), required: true)));

        await AssertWidgetRefusalAsync(document, "location sibling");
    }

    /// <summary>
    /// The array arm: an inline-object list item promotes with the same operation-scoped name a
    /// single-object 'data' payload promotes under (the promotion guard exists to stop a
    /// pointer-derived name leaking, not to forbid promotion outright once one is registered).
    /// </summary>
    [Test]
    public async Task Bind_Should_Promote_A_Data_Location_List_Of_Inline_Items_Under_An_Operation_Scoped_Name()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario(
            data: static property => property
                .Type("array")
                .Items(static item => item
                    .Type("object")
                    .Property("id", static inner => inner.Type("string"), required: true))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocationList);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        var elementType = (NamedTypeReferencePlan)((ListTypeReferencePlan)list.Envelope.PayloadType!).ElementType;
        await Assert.That(elementType.Name).IsEqualTo("WidgetListData");
        await AssertPromotedListDataAsync(plan);
    }

    /// <summary>
    /// The single-object arm: a location wrapper whose 'data' is an inline object promotes
    /// under the same operation-scoped name the list arm's items take, so the wrapper's own
    /// spelling never reaches the surface and the envelope stays a single-value DataLocation.
    /// </summary>
    [Test]
    public async Task Bind_Should_Promote_A_Data_Location_Inline_Object_Under_An_Operation_Scoped_Name()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario(
            data: static property => property
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("output", static inner => inner.Type("string"), required: true)
                .Property("truncated", static inner => inner.Type("boolean"), required: true)));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocation);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)list.Envelope.PayloadType!).Name).IsEqualTo("WidgetListData");
        await Assert.That(list.Envelope.LocationTypeName).IsEqualTo("PlaceInfo");
        await AssertPromotedListDataAsync(plan);
    }

    /// <summary>
    /// The name resolver, not the type machinery's mechanical fallback, is what claims the
    /// promoted single object: a wrapper two selected operations share reaches the same
    /// double-claim wall the bare root and the list item already reach.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Promoted_Data_Location_Inline_Object_Claimed_By_Two_Operations()
    {
        var document = await BindingTestHost.IngestAsync(WidgetPayloadConflictScenario(property => property
            .Type("object").Property("id", static inner => inner.Type("string"), required: true)));

        await AssertWidgetPayloadConflictAsync(document);
    }

    /// <summary>
    /// Promotion is not a licence for every RefNode: a promoted 'data' the resolver leaves
    /// unnamed — here a structural union that collapses to one primitive, which is no type of
    /// its own and never claims a C# name — still falls through to the unchanged refusal
    /// rather than resurfacing under a mechanically-derived name.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Promoted_Data_Location_Payload_The_Resolver_Leaves_Unnamed()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario(
            data: static property => property.AnyOf(
                static branch => branch.Type("string").Raw("pattern", "\"^a\""),
                static branch => branch.Type("string").Raw("pattern", "\"^b\""))));

        await AssertWidgetRefusalAsync(document, "named component schema");
    }

    /// <summary>
    /// A RefNode item that resolves to a non-nominal target (a primitive alias, here) is
    /// claimed by neither the nominal-component lookup nor the promotion resolver: it is refused.
    /// Promotion narrows the guard to inline objects; it does not give every RefNode a
    /// mechanically derived name.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Data_Location_List_Item_Referencing_A_Non_Nominal_Component()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetAlias", schema => schema.Type("string"))
            .WithSchema("PlaceInfo", schema => schema
                .Type("object")
                .Property("directory", property => property.Type("string"), required: true))
            .WithSchema("WidgetEnvelope", schema => DefineLocationEnvelope(schema,
                property => property.Type("array").Items(static item => item.Ref("WidgetAlias"))))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetEnvelope")))));

        await AssertWidgetRefusalAsync(document, "named component schema");
    }

    /// <summary>
    /// The array-ref extension: 'data' can name an ARRAY component directly (vcs.branches'
    /// exact shape — a $ref to a named array-of-string component) rather than wrapping the
    /// array inline. The resolved target's item is a primitive, which the type machinery binds
    /// through its ordinary RefNode -&gt; ArrayNode -&gt; primitive path once the guard stops
    /// refusing the ref.
    /// </summary>
    [Test]
    public async Task Bind_Should_Bind_A_Data_Location_List_Envelope_Whose_Data_Refs_A_Named_Array_Component()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("PlaceInfo", DefinePlaceInfo)
            .WithSchema("WidgetNameList", schema => schema
                .Type("array")
                .Items(static item => item.Type("string")))
            .WithSchema("WidgetEnvelope", schema => DefineLocationEnvelope(schema, property => property.Ref("WidgetNameList")))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetEnvelope")))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocationList);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        var elementType = (NamedTypeReferencePlan)((ListTypeReferencePlan)list.Envelope.PayloadType!).ElementType;
        await Assert.That(elementType.Name).IsEqualTo("string");
        await Assert.That(list.Envelope.LocationTypeName).IsEqualTo("PlaceInfo");
    }

    /// <summary>
    /// The array-ref extension narrows the guard by shape (ref resolving to an array), it does
    /// not widen it for every RefNode: a 'data' ref resolving to a NOMINAL (object) target
    /// excluded from naming — here, the wrapper's own response root, referenced from within
    /// itself — still falls through to the unchanged refusal below. No resurrection for a
    /// non-array ref, exactly as the original guard intended.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Data_Location_Ref_Resolving_To_A_Nominal_Object_With_No_Name()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("PlaceInfo", DefinePlaceInfo)
            .WithSchema("WidgetEnvelope", schema => DefineLocationEnvelope(schema, property => property.Ref("WidgetEnvelope")))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetEnvelope")))));

        await AssertWidgetRefusalAsync(document, "named component schema");
    }

    /// <summary>
    /// <c>OperationFacetContext.Resolve</c> only follows RefNode chains: it does not
    /// unwrap a NullableNode. A ref resolving to a NULLABLE array (an in-band optional wrapping
    /// the array shape, not the array itself) therefore does not match the array-ref arm's
    /// pattern and falls through unmatched to the unchanged guard below, refusing exactly as
    /// before — this dialect has no NullableNode-unwrapping step of its own yet (unlike
    /// QueryRequestFacetBinder/SpineShapePolicy, which unwrap it as an explicit separate step).
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Data_Location_Ref_Resolving_To_A_Nullable_Array()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("PlaceInfo", DefinePlaceInfo)
            .WithSchema("NullableWidgetNameList", schema => schema.AnyOf(
                static branch => branch.Type("array").Items(static item => item.Type("string")),
                static branch => branch.Type("null")))
            .WithSchema("WidgetEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("location", property => property.Ref("PlaceInfo"), required: true)
                .Property("data", property => property.Ref("NullableWidgetNameList"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetEnvelope")))));

        await AssertWidgetRefusalAsync(document, "named component schema");
    }

    /// <summary>Reuses the single-object payload's double-claim wall for the array arm's promoted item.</summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Promoted_Data_Location_List_Item_Claimed_By_Two_Operations()
    {
        var document = await BindingTestHost.IngestAsync(WidgetPayloadConflictScenario(property => property
            .Type("array").Items(static item => item.Type("object")
                .Property("id", static inner => inner.Type("string"), required: true))));

        await AssertWidgetPayloadConflictAsync(document);
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Envelope_Dto_Name_Colliding_With_A_Model()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true)
                .Property("extra", property => property.Ref("WidgetListResponseEnvelope"), required: true))
            .WithSchema("WidgetListResponseEnvelope", schema => schema
                .Type("object")
                .Property("note", property => property.Type("string"), required: true))
            .WithSchema("WidgetsResponse", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property
                    .Type("array")
                    .Items(static item => item.Ref("WidgetInfo")), required: true)
                .Property("cursor", cursor => cursor
                    .Type("object")
                    .AdditionalPropertiesFalse()
                    .Property("previous", static property => property.AnyOf(
                        static branch => branch.Type("string"),
                        static branch => branch.Type("null")))
                    .Property("next", static property => property.AnyOf(
                        static branch => branch.Type("string"),
                        static branch => branch.Type("null"))), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetsResponse")))));

        var exception = Assert.Throws<BindingException>(() => _ = BindWidgets(document));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
            && error.Problem.Contains("envelope DTO", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Name_A_Component_Request_Body_From_The_Operation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("Widget.CreatePayload", schema => schema
                .Type("object")
                .Property("title", property => property.Type("string"), required: true))
            .WithOperation("widget.create", method: "post", path: "/api/widget", configure: operation => operation
                .RequestBody("application/json", body => body.Ref("Widget.CreatePayload"), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        var plan = BindWidgets(document, "widget.create");

        _ = await AssertCreateRequestAsync(plan);
        await Assert.That(plan.Models).Contains(static model => model.Name == "WidgetCreateRequest");
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name == "WidgetCreatePayload");
        await Assert.That(plan.Registry.TypeNames).Contains("WidgetCreateRequest", StringComparer.Ordinal);
    }

    [Test]
    public async Task Bind_Should_Not_Let_A_Pending_Operation_Rename_A_Shared_Component()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetShared", schema => schema
                .Type("object")
                .Property("note", property => property.Type("string"), required: true))
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true)
                .Property("shared", property => property.Ref("WidgetShared"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))
            .WithOperation("widget.create", method: "post", path: "/api/widget-create", configure: operation => operation
                .RequestBody("application/json", body => body.Ref("WidgetShared"), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        var plan = BindWidgets(document);

        await Assert.That(plan.Models).Contains(static model => model.Name == "WidgetShared");
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name == "WidgetCreateRequest");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Operation_Mixing_A_Body_And_Query_Parameters()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("Widget.CreatePayload", schema => schema
                .Type("object")
                .Property("title", property => property.Type("string"), required: true))
            .WithOperation("widget.create", method: "post", path: "/api/widget", configure: operation => operation
                .Parameter("search", "query", static schema => schema.AnyOf(
                    static branch => branch.Type("string"),
                    static branch => branch.Type("null")), required: false)
                .RequestBody("application/json", body => body.Ref("Widget.CreatePayload"), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        await AssertWidgetRefusalAsync(document, "request body and query", "widget.create", strings: ["search"]);
    }

    [Test]
    public async Task Bind_Should_Bind_A_Component_Data_Envelope_Without_Modeling_The_Wrapper()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetResponse", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property.Ref("WidgetInfo"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetResponse")))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)list.Envelope.PayloadType!).Name).IsEqualTo("WidgetInfo");
        await Assert.That(plan.Models.Select(static model => model.Name))
            .IsEquivalentTo(["WidgetInfo"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Accept_A_Data_Envelope_Wrapping_A_List_Of_Named_Models()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetListEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property.Type("array").Items(item => item.Ref("WidgetInfo")), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetListEnvelope")))));

        var plan = BindWidgets(document, "widget.list");

        var operation = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(operation.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(operation.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(((ListTypeReferencePlan)operation.Envelope.PayloadType!).ElementType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((ListTypeReferencePlan)operation.Envelope.PayloadType).ElementType).Name)
            .IsEqualTo("WidgetInfo");
    }

    [Test]
    public async Task Bind_Should_Accept_A_Data_Envelope_Wrapping_A_Dictionary_Of_Named_Models()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetMapEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property.Type("object").AdditionalProperties(value => value.Ref("WidgetInfo")),
                    required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetMapEnvelope")))));

        var plan = BindWidgets(document, "widget.list");

        var operation = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(operation.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(operation.Envelope.PayloadType).IsTypeOf<DictionaryTypeReferencePlan>();
        await Assert.That(((DictionaryTypeReferencePlan)operation.Envelope.PayloadType!).ValueType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((DictionaryTypeReferencePlan)operation.Envelope.PayloadType).ValueType).Name)
            .IsEqualTo("WidgetInfo");
    }

    [Test]
    public async Task Bind_Should_Accept_A_Bare_Envelope_That_Is_A_List_Of_Named_Models()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Type("array").Items(item => item.Ref("WidgetInfo"))))));

        var plan = BindWidgets(document, "widget.list");

        var operation = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(operation.Envelope!.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(operation.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
        await Assert.That(((ListTypeReferencePlan)operation.Envelope.PayloadType!).ElementType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((ListTypeReferencePlan)operation.Envelope.PayloadType).ElementType).Name)
            .IsEqualTo("WidgetInfo");
    }

    [Test]
    public async Task Bind_Should_Accept_A_Bare_Envelope_That_Is_A_Dictionary_Of_Named_Models()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Type("object").AdditionalProperties(value => value.Ref("WidgetInfo"))))));

        var plan = BindWidgets(document, "widget.list");

        var operation = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(operation.Envelope!.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(operation.Envelope.PayloadType).IsTypeOf<DictionaryTypeReferencePlan>();
        await Assert.That(((DictionaryTypeReferencePlan)operation.Envelope.PayloadType!).ValueType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((DictionaryTypeReferencePlan)operation.Envelope.PayloadType).ValueType).Name)
            .IsEqualTo("WidgetInfo");
    }

    /// <summary>
    /// A one-property inline body belongs to the single-key facet, so the promotion row carries
    /// two properties: what it pins is the operation-scoped name of a promoted bare payload.
    /// </summary>
    [Test]
    public async Task Bind_Should_Promote_An_Inline_Object_Bare_Payload_Under_An_Operation_Scoped_Name()
    {
        var document = await BindingTestHost.IngestAsync(InlinePayloadScenario(static schema => schema
            .Property("count", static property => property.Type("integer"), required: true)
            .Property("label", static property => property.Type("string"), required: true)));

        var plan = BindWidgets(document, "widget.stats");

        var stats = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(stats.Envelope!.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(stats.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(((NamedTypeReferencePlan)stats.Envelope.PayloadType!).Name).IsEqualTo("WidgetStatsData");
        await Assert.That(plan.Models).Contains(static model => string.Equals(model.Name, "WidgetStatsData", StringComparison.Ordinal));
        await Assert.That(plan.Registry.TypeNames).Contains("WidgetStatsData", StringComparer.Ordinal);
        await Assert.That(plan.Registry.PayloadEntries.Count).IsEqualTo(0);
    }

    /// <summary>
    /// An inline body requiring exactly one non-<c>data</c> property is envelope spine wearing
    /// its payload's key: the value flattens onto the response under the key, and the wrapper
    /// never becomes a model.
    /// </summary>
    [Test]
    public async Task Bind_Should_Flatten_An_Inline_Single_Key_Wrapper_Onto_Its_Own_Key()
    {
        var document = await BindingTestHost.IngestAsync(InlinePayloadScenario(static schema => schema
            .Property("handoff", static property => property.AnyOf(
                static branch => branch.Ref("WidgetInfo"),
                static branch => branch.Type("null")), required: true)));

        var plan = BindWidgets(document, "widget.stats");

        var stats = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(stats.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(stats.Envelope.WireMemberName).IsEqualTo("handoff");
        await Assert.That(stats.Envelope.PayloadName).IsEqualTo("Handoff");
        await Assert.That(((NamedTypeReferencePlan)stats.Envelope.PayloadType!).Name).IsEqualTo("WidgetInfo");
        await Assert.That(stats.Envelope.PayloadType.IsNullable).IsTrue();
        await Assert.That(plan.Models).DoesNotContain(static model => string.Equals(model.Name, "WidgetStatsData", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>EnvelopeClassifier</c> admits a single non-<c>data</c> key on key count alone,
    /// whether or not that key is required — requiredness is deliberately a binding fact, not a
    /// classification one. An inline single-key wrapper whose sole property is optional has no
    /// payload the envelope can promise, so <c>EnvelopeFacetBinder.SingleKeyMember</c> is the
    /// wall that refuses it by name instead of silently falling back to a bare body.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_An_Inline_Single_Key_Wrapper_Whose_Sole_Property_Is_Optional()
    {
        var document = await BindingTestHost.IngestAsync(InlinePayloadScenario(static schema => schema
            .Property("handoff", static property => property.AnyOf(
                static branch => branch.Ref("WidgetInfo"),
                static branch => branch.Type("null")), required: false)));

        await AssertWidgetRefusalAsync(
            document,
            "single-key envelope must reference an object requiring exactly one property",
            "widget.stats");
    }

    [Test]
    public async Task Bind_Should_Name_A_Data_Envelope_Promoted_Payload_From_The_Operation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetStatsEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property
                    .Type("object")
                    .AdditionalPropertiesFalse()
                    .Property("count", inner => inner.Type("integer"), required: true), required: true))
            .WithOperation("widget.stats", path: "/api/widget/stats", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetStatsEnvelope")))));

        var plan = BindWidgets(document, "widget.stats");

        var stats = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(stats.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(((NamedTypeReferencePlan)stats.Envelope.PayloadType!).Name).IsEqualTo("WidgetStatsData");
        await Assert.That(plan.Models).Contains(static model => string.Equals(model.Name, "WidgetStatsData", StringComparison.Ordinal));

        // The wrapper component's own spelling never reaches the surface.
        await Assert.That(plan.Models).DoesNotContain(static model => model.Name.Contains("Envelope", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Promoted_Payload_Claimed_By_Two_Operations()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetStatsEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property
                    .Type("object")
                    .AdditionalPropertiesFalse()
                    .Property("count", inner => inner.Type("integer"), required: true), required: true))
            .WithOperation("widget.stats", path: "/api/widget/stats", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetStatsEnvelope")))
            .WithOperation("widget.summary", path: "/api/widget/summary", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetStatsEnvelope")))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("widget.stats", "widget.summary"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)))));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
                                                       && error.Problem.Contains(
                                                           "claimed as both 'WidgetStatsData' and 'WidgetSummaryData'",
                                                           StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Promoted_Payload_Whose_Name_Collides_With_A_Component()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetStatsData", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.stats", path: "/api/widget/stats", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    // Two properties keep the body a promoted bare payload; one would be the
                    // single-key facet's, which promotes no model to collide.
                    .Property("related", property => property.Ref("WidgetStatsData"), required: true)
                    .Property("count", property => property.Type("integer"), required: true)))));

        var exception = Assert.Throws<BindingException>(() => _ = BindWidgets(document, "widget.stats"));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
                                                       && error.Problem.Contains(
                                                           "C# type name 'WidgetStatsData' collides with schema",
                                                           StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Accept_A_Data_Location_Envelope_Whose_Data_Is_A_Dictionary()
    {
        var document = await BindingTestHost.IngestAsync(DataLocationScenario(
            data: static property => property.Type("object").AdditionalProperties(value => value.Ref("WidgetInfo"))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.DataLocation);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<DictionaryTypeReferencePlan>();
        await Assert.That(((DictionaryTypeReferencePlan)list.Envelope.PayloadType!).ValueType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert
            .That(((NamedTypeReferencePlan)((DictionaryTypeReferencePlan)list.Envelope.PayloadType).ValueType).Name)
            .IsEqualTo("WidgetInfo");
        await Assert.That(list.Envelope.LocationTypeName).IsEqualTo("PlaceInfo");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Data_Envelope_Whose_Payload_Is_An_Unsupported_Node()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetTupleEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property
                    .Type("array")
                    .PrefixItems(item => item.Type("string"))
                    .MinItems(1)
                    .MaxItems(1), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetTupleEnvelope")))));

        await AssertWidgetRefusalAsync(document, "does not bind to a supported type plan");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Data_Envelope_Whose_Payload_Is_Nullable()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("WidgetResponse", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("data", property => property.AnyOf(
                    static branch => branch.Ref("WidgetInfo"),
                    static branch => branch.Type("null")), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetResponse")))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.Kind).IsEqualTo(EnvelopeKind.Data);
        await Assert.That(list.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        await Assert.That(list.Envelope.PayloadType!.IsNullable).IsTrue();
        await Assert.That(((NamedTypeReferencePlan)list.Envelope.PayloadType).Name).IsEqualTo("WidgetInfo");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Bare_Envelope_Whose_Payload_Is_Nullable()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.AnyOf(
                    static branch => branch.Ref("WidgetInfo"),
                    static branch => branch.Type("null"))))));

        await AssertWidgetRefusalAsync(document, "a bare success body cannot represent null");
    }

    /// <summary>
    /// A promoted payload's operation-scoped stem is a concept name, not a surfaced
    /// type name outright: when the promoted schema is itself a Marked union, the stem must
    /// still route through <c>ToUnionInterfaceName</c> so it gets the same <c>I</c> prefix
    /// every other Marked union interface carries (ADR-0011), rather than surfacing bare.
    /// </summary>
    [Test]
    public async Task Bind_Should_Promote_A_Bare_Marked_Union_Payload_With_An_I_Prefixed_Name()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("widget.condition", path: "/api/widget-condition", configure: operation => operation
                .Response(200, "application/json", schema => schema.AnyOf(
                    static branch => branch
                        .Type("object")
                        .AdditionalPropertiesFalse()
                        .Property("_tag", static inner => inner.Type("string").Enum("Alpha"), required: true)
                        .Property("value", static inner => inner.Type("string"), required: true),
                    static branch => branch
                        .Type("object")
                        .AdditionalPropertiesFalse()
                        .Property("_tag", static inner => inner.Type("string").Enum("Beta"), required: true))))));

        var plan = BindWidgets(document, "widget.condition");

        var condition = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(condition.Envelope!.Kind).IsEqualTo(EnvelopeKind.Bare);
        await Assert.That(condition.Envelope.PayloadType).IsTypeOf<NamedTypeReferencePlan>();
        var payloadTypeName = ((NamedTypeReferencePlan)condition.Envelope.PayloadType!).Name;
        await Assert.That(payloadTypeName).IsEqualTo("IWidgetConditionData");
        await Assert.That(plan.Unions).Contains(union => union.Name == "IWidgetConditionData" && union.ConceptName == "WidgetConditionData");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Cursor_List_With_A_Malformed_Cursor()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(cursor => cursor
            .Type("object")
            .AdditionalPropertiesFalse()
            .Property("previous", property => property.AnyOf(
                static branch => branch.Type("string"),
                static branch => branch.Type("null")), required: true)
            .Property("next", property => property.AnyOf(
                static branch => branch.Type("string"),
                static branch => branch.Type("null")))));

        await AssertWidgetRefusalAsync(document, "cursor object");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Cursor_List_Whose_Items_Are_Not_Component_References()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(items: items => items
            .Type("object")
            .Property("id", property => property.Type("string"), required: true)));

        await AssertWidgetRefusalAsync(document, "array of a named component schema");
    }

    [Test]
    public async Task Bind_Should_Merge_Groups_Sharing_A_Client_Name()
    {
        var document = await BindingTestHost.IngestAsync(MergedGadgetScenario());

        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["gadget"] = ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID"),
            ["gizmo"] = ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID"),
        };

        var plan = new BindingTestHost().Bind(
            document,
            Selection("gadget.part", "gizmo.list"),
            Curation(groups));

        var root = plan.Clients.Single(static client => client.Role == ClientRole.Root);
        await Assert.That(root.SubClients.Single().TypeName).IsEqualTo("GadgetsClient");
        var collection = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        await Assert.That(collection.Operations).IsEmpty();
        // On a handle the family is the subject, so the verb-only gizmo.list keeps no group.
        var handle = plan.Clients.Single(static client => client.Role == ClientRole.Handle);
        await Assert.That(handle.Operations.Select(static operation => operation.MethodName).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["GetPartAsync", "ListAsync"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(handle.Operations).All().Satisfy(operation => operation.RouteContainerName, value => value.IsEqualTo("Gadgets"));
    }

    [Test]
    public async Task Bind_Should_Refuse_Merged_Groups_With_Diverging_Handles()
    {
        var document = await BindingTestHost.IngestAsync(MergedGadgetScenario());

        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["gadget"] = ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID"),
            ["gizmo"] = ClientGroup(clientName: "Gadgets", handleName: null, handleParameter: null),
        };

        var exception = RefuseMergedGadget(document, groups);

        await AssertProblemAsync(exception, BindingErrorCategory.Curation, "identical handle");
    }

    /// <summary>
    /// A merged family takes its emission from one row but the header wall reads each
    /// operation's own row, so a divergent pair would emit one row's accessibility over the
    /// other's operations — an admitted header could land on a public client.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_Merged_Groups_With_Diverging_Emission()
    {
        var document = await BindingTestHost.IngestAsync(MergedGadgetScenario(gizmoDeclaresHeader: true));

        // 'gadget' sorts first, so an unrefused family would take its public emission while
        // 'gizmo' contributes the header its own internalRaw row admitted.
        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["gadget"] = ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID"),
            ["gizmo"] = ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID",
                emission: EmissionMode.InternalRaw),
        };

        var exception = RefuseMergedGadget(document, groups);

        await AssertProblemAsync(exception, BindingErrorCategory.Curation, "identical handle and emission");
    }

    [Test]
    public async Task Bind_Should_Merge_Agreeing_Internal_Raw_Groups_Into_One_Raw_Family()
    {
        var document = await BindingTestHost.IngestAsync(MergedGadgetScenario(gizmoDeclaresHeader: true));

        var groups = new Dictionary<string, GroupCuration>(StringComparer.Ordinal)
        {
            ["gadget"] = ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID",
                emission: EmissionMode.InternalRaw),
            ["gizmo"] = ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID",
                emission: EmissionMode.InternalRaw),
        };

        var plan = new BindingTestHost().Bind(
            document,
            Selection("gadget.part", "gizmo.list"),
            Curation(groups));

        var root = plan.Clients.Single(static client => client.Role == ClientRole.Root);
        await Assert.That(root.SubClients.Single().TypeName).IsEqualTo("GadgetsClient");
        var collection = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        await Assert.That(collection.Name).IsEqualTo("GadgetsRawClient");
        await Assert.That(collection.Emission).IsEqualTo(EmissionMode.InternalRaw);
        var handle = plan.Clients.Single(static client => client.Role == ClientRole.Handle);
        await Assert.That(handle.Name).IsEqualTo("GadgetRawClient");
        await Assert.That(handle.Emission).IsEqualTo(EmissionMode.InternalRaw);
        await Assert.That(handle.Operations.Select(static operation => operation.MethodName).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["GetPartAsync", "ListAsync"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert
            .That(handle
                .Operations.Single(static operation => operation.MethodName == "ListAsync")
                .DeclaredHeaders.Single()
                .Name)
            .IsEqualTo("xOpencodeTicket");
    }

    [Test]
    public async Task Bind_Should_Name_An_Internal_Raw_Family_Beside_Its_Public_Family_Accessor()
    {
        var document = await BindingTestHost.IngestAsync(PtyScenario());

        var plan = BindPtys(document, EmissionMode.InternalRaw);

        var root = plan.Clients.Single(static client => client.Role == ClientRole.Root);
        await Assert.That(root.Emission).IsEqualTo(EmissionMode.Public);
        await Assert.That(root.SubClients.Single().PropertyName).IsEqualTo("Ptys");
        await Assert.That(root.SubClients.Single().TypeName).IsEqualTo("PtysClient");

        var collection = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        await Assert.That(collection.Name).IsEqualTo("PtysRawClient");
        await Assert.That(collection.Emission).IsEqualTo(EmissionMode.InternalRaw);
        await Assert.That(collection.ContainerName).IsEqualTo("Ptys");
        await Assert.That(collection.HandleFactory!.HandleTypeName).IsEqualTo("PtyRawClient");
        await Assert.That(collection.HandleFactory.MethodName).IsEqualTo("GetPtyRawClient");

        var handle = plan.Clients.Single(static client => client.Role == ClientRole.Handle);
        await Assert.That(handle.Name).IsEqualTo("PtyRawClient");
        await Assert.That(handle.Emission).IsEqualTo(EmissionMode.InternalRaw);
        await Assert.That(handle.ContainerName).IsEqualTo("Ptys");
        await Assert.That(handle.Operations).All().Satisfy(operation => operation.RouteContainerName, value => value.IsEqualTo("Ptys"));
    }

    [Test]
    public async Task Bind_Should_Carry_A_Declared_Header_On_An_Internal_Raw_Operation()
    {
        var document = await BindingTestHost.IngestAsync(PtyScenario());

        var plan = BindPtys(document, EmissionMode.InternalRaw);

        var handle = plan.Clients.Single(static client => client.Role == ClientRole.Handle);
        var token = handle.Operations.Single(static operation => operation.MethodName == "CreateConnectTokenAsync");
        var header = token.DeclaredHeaders.Single();
        await Assert.That(header.WireName).IsEqualTo("x-opencode-ticket");
        await Assert.That(header.Name).IsEqualTo("xOpencodeTicket");
        await Assert
            .That(handle.Operations.Single(static operation => operation.MethodName == "GetAsync").DeclaredHeaders)
            .IsEmpty();
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Header_Parameter_On_A_Public_Family()
    {
        var document = await BindingTestHost.IngestAsync(PtyScenario());

        var exception = Assert.Throws<BindingException>(() => _ = BindPtys(document, EmissionMode.Public));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Operation
                                                       && error.Problem.Contains(
                                                           "header parameter 'x-opencode-ticket' has no runtime channel",
                                                           StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Required_Header_Parameter_On_An_Internal_Raw_Family()
    {
        var document = await BindingTestHost.IngestAsync(PtyScenario(headerRequired: true));

        var exception = Assert.Throws<BindingException>(() => _ = BindPtys(document, EmissionMode.InternalRaw));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Operation
                                                       && error.Problem.Contains(
                                                           "must be optional, not deep-object, and declare a nullable string schema",
                                                           StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Omit_Only_Cursor_Pagination_On_An_Operation_Declaring_A_Header()
    {
        var document = await BindingTestHost.IngestAsync(CursorListScenario(configure: static operation => CursorQuery(operation)
            .Parameter("x-opencode-ticket", "header", QueryScenarioData.NullableString)));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.list"),
            Curation(Groups(
                "widget",
                ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null, emission: EmissionMode.InternalRaw)),
            queryParameterTypes: [QueryParameterType("widget.list", "limit", "int")],
            stringQueryParameters: [StringQueryParameter("widget.list", "cursor")]));
        var client = plan.Clients.Single(static client => client.Role == ClientRole.Collection);
        var list = client.Operations.Single();
        await Assert.That(client.Name).IsEqualTo("WidgetsRawClient");
        await Assert.That(list.MethodName).IsEqualTo("ListWidgetsAsync");
        await Assert.That(list.DeclaredHeaders).IsEquivalentTo([
            new DeclaredHeaderPlan { WireName = "x-opencode-ticket", Name = "xOpencodeTicket" },
        ], CollectionOrdering.Matching);
        await Assert.That(list.Pagination).IsNull();
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Header_Parameter_Landing_On_A_Reserved_Emitted_Name()
    {
        var document = await BindingTestHost.IngestAsync(PtyScenario(headerName: "declared-headers"));

        var exception = Assert.Throws<BindingException>(() => _ = BindPtys(document, EmissionMode.InternalRaw));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
                                                       && error.Problem.Contains(
                                                           "'declaredHeaders' is reserved",
                                                           StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Bind_A_500_Error_Arm()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("UnknownFailure", schema => schema
                .Type("object")
                .Property("_tag", property => property.Type("string").Enum("UnknownFailure"), required: true)
                .Property("message", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo"))
                .Response(500, "application/json", schema => schema.Ref("UnknownFailure")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("health.get"),
            Curation(Groups("health", RootGroup())));

        var health = plan.Clients.Single(static client => client.Role == ClientRole.Root).Operations.Single();
        var status = health.ErrorMap.Statuses.Single();
        await Assert.That(status.StatusCode).IsEqualTo(500);
        await Assert.That(status.Tags.Single().TypeName).IsEqualTo("UnknownFailure");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Required_Query_Parameter_As_A_Required_Text_Property()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("query", "query", schema => schema.Type("string"), required: true)));

        var plan = BindWidgets(document, strings: ["query"]);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var property = list.QueryRequest!.Properties.Single();
        await Assert.That(property.PropertyName).IsEqualTo("Query");
        await Assert.That(property.Kind).IsEqualTo(QueryValueKind.Text);
        await Assert.That(property.IsRequired).IsTrue();
        await Assert.That(list.QueryRequest.HasRequiredMember).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Bind_An_Optional_Query_Parameter_That_Does_Not_Admit_Null_Like_One_That_Does()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("limit", "query", schema => schema.Type("string"))));

        var plan = BindWidgets(document, strings: ["limit"]);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var property = list.QueryRequest!.Properties.Single();
        await Assert.That(property.PropertyName).IsEqualTo("Limit");
        await Assert.That(property.Kind).IsEqualTo(QueryValueKind.Text);
        await Assert.That(property.IsRequired).IsFalse();
        await Assert.That(list.QueryRequest.HasRequiredMember).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Bind_One_Or_Many_Of_An_Enum_As_An_Enum_List(bool required)
    {
        var document = await IngestModesAsync(static item => item.Ref("Widget.Mode"), required);

        var property = BindWidgets(document).Clients.SelectMany(static client => client.Operations).Single().QueryRequest!.Properties.Single();
        await Assert.That((property.Kind, property.EnumTypeName, property.IsRequired)).IsEqualTo((QueryValueKind.EnumList, "WidgetMode", required));
    }

    /// <summary>A list whose item is another enum or no enum, or a value-or-list of a plain string, is not a value-or-list of the enum.</summary>
    [Test]
    [Arguments("Widget.Other", false)]
    [Arguments("string", false)]
    [Arguments("string", true)]
    public async Task Bind_Should_Refuse_A_List_Whose_Item_Is_Not_The_Single_Enum(string item, bool valueIsItem)
    {
        Action<SchemaBuilder> listItem = schema => _ = item == "string" ? schema.Type("string") : schema.Ref(item);
        var document = await IngestModesAsync(listItem, required: false, valueIsItem ? listItem : null);

        var exception = Assert.Throws<BindingException>(() => _ = BindWidgets(document));
        await Assert.That(exception.Errors).IsEquivalentTo(
            [new BindingError(BindingErrorCategory.Operation, "widget.list", "query parameter 'modes' has an unsupported schema shape")],
            CollectionOrdering.Matching);
    }

    private static Task<SpecDocument> IngestModesAsync(Action<SchemaBuilder> listItem, bool required, Action<SchemaBuilder>? value = null) =>
        BindingTestHost.IngestAsync(WidgetListScenario(
            operation => operation.Parameter("modes", "query", schema => _ = required
                ? schema.AnyOf(value ?? (static branch => branch.Ref("Widget.Mode")), branch => branch.Type("array").Items(listItem))
                : schema.AnyOf(value ?? (static branch => branch.Ref("Widget.Mode")), branch => branch.Type("array").Items(listItem), static branch => branch.Type("null")),
                required: required),
            static spec => spec
                .WithSchema("Widget.Mode", static schema => schema.Type("string").Enum("working", "branch"))
                .WithSchema("Widget.Other", static schema => schema.Type("string").Enum("other"))));

    [Test]
    public async Task Bind_Should_Bind_A_Referenced_Enum_Query_Parameter_To_Its_Component_Model()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("Widget.Mode", schema => schema.Type("string").Enum("working", "branch"))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Parameter("mode", "query", schema => schema.Ref("Widget.Mode"), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var property = list.QueryRequest!.Properties.Single();
        await Assert.That(property.Kind).IsEqualTo(QueryValueKind.Enum);
        await Assert.That(property.EnumTypeName).IsEqualTo("WidgetMode");
        await Assert.That(property.IsRequired).IsTrue();
        await Assert.That(plan.Models.Select(static model => model.Name).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["WidgetInfo", "WidgetMode"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Bind_An_Inline_Enum_Query_Parameter_To_A_Mechanically_Named_Model()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("type", "query", static schema => schema.AnyOf(
                static branch => branch.Type("string").Enum("file", "directory"),
                static branch => branch.Type("null")))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var property = list.QueryRequest!.Properties.Single();
        await Assert.That(property.Kind).IsEqualTo(QueryValueKind.Enum);
        await Assert.That(property.EnumTypeName).IsEqualTo("WidgetListRequestType");
        await Assert.That(property.IsRequired).IsFalse();
        var model = (EnumModelPlan)plan.Models.Single(static model => model.Name is "WidgetListRequestType");
        await Assert.That(model.Values.Select(static value => value.WireValue))
            .IsEquivalentTo(["file", "directory"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A format on an enum is a dialect the SDK does not read, so the parameter is not treated as
    /// an enum at all and lands on the ordinary value path, which has no shape for it either. The
    /// refusal is by name rather than a silent bind against a model that was never generated.
    /// </summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Formatted_Enum_Query_Parameter()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("mode", "query", static schema => schema.Type("string").Enum("working", "branch").Format("mode"))));

        var exception = Assert.Throws<BindingException>(() => _ = BindWidgets(document));

        await Assert.That(exception.Errors).Contains(static error =>
            error.Problem.Contains("query parameter 'mode' has an unsupported schema shape", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Keep_A_Spine_Profile_Query_Enum_Out_Of_The_Model_Closure()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("order", "query", QueryScenarioData.NullableOrderEnum)));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.QueryRequest!.Properties.Single().Kind).IsEqualTo(QueryValueKind.ListOrder);
        await Assert.That(plan.Models.Select(static model => model.Name))
            .IsEquivalentTo(["WidgetInfo"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Formatted_Query_Without_A_Declared_Mapping()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("target", "query", schema => schema.AnyOf(
                branch => branch.Type("string").Format("uri"),
                branch => branch.Type("null")))));

        await AssertWidgetRefusalAsync(document, "unsupported schema shape");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Formatted_Parent_Filter_Branch()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("parentID", "query", QueryScenarioData.NullableFormattedParentFilter)));

        await AssertWidgetRefusalAsync(document, "unsupported schema shape");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Formatted_Location_Selector_Member()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("location", "query", QueryScenarioData.NullableFormattedLocationSelector, deepObject: true)));

        await AssertWidgetRefusalAsync(document, "outside the optional location selector shape");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Near_Spine_Query_Enum_As_Its_Own_Model()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("order", "query", schema => schema.AnyOf(
                branch => branch.Type("string").Enum("asc", "desc", "shuffled"),
                branch => branch.Type("null")))));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var property = list.QueryRequest!.Properties.Single();
        await Assert.That(property.Kind).IsEqualTo(QueryValueKind.Enum);
        await Assert.That(property.EnumTypeName).IsEqualTo("WidgetListRequestOrder");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Object_Query_Parameter()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("filter", "query", schema => schema.AnyOf(
                branch => branch
                    .Type("object")
                    .Property("name", property => property.Type("string"), required: true),
                branch => branch.Type("null")))));

        await AssertWidgetRefusalAsync(document, "unsupported schema shape");
    }

    [Test]
    public async Task Bind_Should_Merge_A_Location_Query_Into_The_Request_Body_Model()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.create", method: "post", path: "/api/widget", configure: operation => operation
                .Parameter("location", "query", QueryScenarioData.NullableLocationSelector, deepObject: true)
                .RequestBody("application/json", body => body
                    .Type("object")
                    .Property("title", property => property.Type("string"), required: true), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        var plan = BindWidgets(document, "widget.create");

        var create = await AssertCreateRequestAsync(plan);
        await Assert.That(create.QueryRequest!.RidesRequestBody).IsTrue();
        await Assert.That(create.QueryRequest.TypeName).IsEqualTo("WidgetCreateRequest");
        await Assert.That(create.QueryRequest.Properties.Single().Kind).IsEqualTo(QueryValueKind.Location);
        var model = (ObjectModelPlan)plan.Models.Single(static model => model.Name is "WidgetCreateRequest");
        await Assert.That(model.RequestQueryProperties.Single().PropertyName).IsEqualTo("Location");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Deep_Object_Query_Parameter()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("location", "query", QueryScenarioData.NullableString, deepObject: true)));

        await AssertWidgetRefusalAsync(document, "deep-object");
    }

    [Test]
    public async Task Bind_Should_Bind_The_Location_Selector_Deep_Object()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("location", "query", QueryScenarioData.NullableLocationSelector, deepObject: true)));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        var property = list.QueryRequest!.Properties.Single();
        await Assert.That(property.Kind).IsEqualTo(QueryValueKind.Location);
        await Assert.That(property.PropertyName).IsEqualTo("Location");
        await Assert.That(plan.Models.Select(static model => model.Name))
            .IsEquivalentTo(["WidgetInfo"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Deep_Object_Outside_The_Location_Selector_Shape()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("location", "query", QueryScenarioData.NullableSelectorWithExtraMember, deepObject: true)));

        await AssertWidgetRefusalAsync(document, "location selector");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Required_Location_Selector()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(operation => operation
            .Parameter("location", "query", QueryScenarioData.NullableLocationSelector, required: true, deepObject: true)));

        await AssertWidgetRefusalAsync(document, "location selector");
    }

    [Test]
    public async Task Bind_Should_Bind_A_Delete_Operation_With_A_No_Content_Success()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("widget.remove", method: "delete", path: "/api/widget/{id}", configure: operation =>
            {
                _ = operation
                    .Parameter("id", "path", schema => schema.Type("string"), required: true)
                    .WithoutResponse(200)
                    .Response(204);
            })));

        var plan = BindWidgets(document, "widget.remove");

        var remove = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(remove.MethodName).IsEqualTo("RemoveWidgetAsync");
        await Assert.That(remove.HttpMethod).IsEqualTo("delete");
        await Assert.That(remove.Envelope!.Kind).IsEqualTo(EnvelopeKind.NoContent);
    }

    /// <summary>
    /// RFC 9110 admits DELETE content when the origin server declares support, and this origin
    /// server declares it in the pinned document, so the shape binds and the body rides the call.
    /// </summary>
    [Test]
    public async Task Bind_Should_Bind_A_Request_Body_On_A_Delete_Operation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("widget.remove", method: "delete", path: "/api/widget", configure: operation => operation
                .RequestBody(
                    "application/json",
                    schema => schema.Type("object").Property("id", property => property.Type("string"), required: true),
                    required: true)
                .WithoutResponse(200)
                .Response(204))));

        var plan = BindWidgets(document, "widget.remove");

        var remove = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(remove.HttpMethod).IsEqualTo("delete");
        await Assert.That(remove.RequestBody!.TypeName).IsEqualTo("WidgetRemoveRequest");
        await Assert.That(remove.RequestBody.IsOptional).IsFalse();
        await Assert.That(remove.Envelope!.Kind).IsEqualTo(EnvelopeKind.NoContent);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Patch_Operation_Without_A_Request_Body()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.timeout", method: "patch", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        await AssertWidgetRefusalAsync(document, "must carry a request body", "widget.timeout");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Put_Operation_Without_A_Request_Body()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.update", method: "put", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        await AssertWidgetRefusalAsync(document, "must carry a request body", "widget.update");
    }

    /// <summary>
    /// HEAD never reaches the body check at all: ingestion refuses the path item's method
    /// outright, body or not, so relaxing the DELETE body arm cannot admit a HEAD body through
    /// the side door.
    /// </summary>
    [Test]
    public async Task Ingest_Should_Refuse_A_Head_Operation_Carrying_A_Request_Body()
    {
        var context = SpecScenario.Define(spec => spec
            .WithOperation("widget.remove", method: "head", path: "/api/widget", configure: operation => operation
                .RequestBody(
                    "application/json",
                    schema => schema.Type("object").Property("id", property => property.Type("string"), required: true),
                    required: true)
                .WithoutResponse(200)
                .Response(204)))
            .Build();

        var exception = await Assert
            .That(async () => _ = await new SpecIngestion(context.FileSystem).IngestAsync(context.SpecPath, CancellationToken.None))
            .Throws<IngestionException>();

        await Assert.That(exception!.Message).Contains("path item method 'head' is not supported");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Request_Body_On_A_Get_Operation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .RequestBody("application/json", schema => schema
                    .Type("object")
                    .Property("value", property => property.Type("string"), required: true))
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        await AssertOperationRefusalAsync(document, "health.get", "must not carry a request body");
    }

    [Test]
    public async Task Bind_Should_Refuse_Multiple_Success_Statuses()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo"))
                .Response(201, "application/json", schema => schema.Ref("ItemInfo")))));

        await AssertOperationRefusalAsync(document, "health.get", "exactly one success response");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Non_200_Success()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .WithoutResponse(200)
                .Response(201, "application/json", schema => schema.Ref("ItemInfo")))));

        await AssertOperationRefusalAsync(document, "health.get", "status 200");
    }

    [Test]
    public async Task Bind_Should_Bind_A_No_Content_Success_Into_A_Payload_Free_Envelope()
    {
        var document = await BindingTestHost.IngestAsync(NoContentScenario());

        var plan = BindWidgets(document, "widget.create");

        var create = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(create.Envelope!.Kind).IsEqualTo(EnvelopeKind.NoContent);
        await Assert.That(create.Envelope.SuccessStatusCode).IsEqualTo(204);
        await Assert.That(create.Envelope.ResponseTypeName).IsEqualTo("WidgetCreateResponse");
        await Assert.That(create.Envelope.PayloadName).IsNull();
        await Assert.That(create.Envelope.PayloadType).IsNull();
        await Assert.That(create.Envelope.EnvelopeDtoTypeName).IsNull();
        await Assert.That(plan.Registry.TypeNames).DoesNotContain("WidgetCreateResponseEnvelope", StringComparer.Ordinal);
    }

    [Test]
    public async Task Bind_Should_Record_The_200_Success_Status_On_The_Envelope()
    {
        var document = await BindingTestHost.IngestAsync(WidgetListScenario(static _ => { }));

        var plan = BindWidgets(document);

        var list = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(list.Envelope!.SuccessStatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_No_Content_Success_Carrying_Content()
    {
        var document = await BindingTestHost.IngestAsync(NoContentScenario(static operation => _ = operation
            .WithoutResponse(204)
            .Response(204, "application/json", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))));

        await AssertWidgetRefusalAsync(document, "must not carry content", "widget.create");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Success_Without_Json_Content()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get")));

        await AssertOperationRefusalAsync(document, "health.get", "JSON schema");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Unsupported_Envelope_Shape()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("data", property => property.Ref("ItemInfo"), required: true)
                    .Property("hasMore", property => property.Type("boolean"), required: true)))));

        await AssertOperationRefusalAsync(document, "health.get", "envelope shape");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Bare_Success_That_Does_Not_Bind_To_A_Type_Plan()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema
                    .Type("string")
                    .ContentSchema("application/json", inner => inner.Ref("ItemInfo"))))));

        await AssertOperationRefusalAsync(document, "health.get", "does not bind to a supported type plan");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Untagged_Error_Response()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("PlainProblem", schema => schema
                .Type("object")
                .Property("message", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo"))
                .Response(404, "application/json", schema => schema.Ref("PlainProblem")))));

        await AssertOperationRefusalAsync(document, "health.get", "tagged error");
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Event_Stream_Whose_Frame_Is_Not_Declared()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .SseResponse(schema => schema.Ref("ItemInfo")))));

        await AssertOperationRefusalAsync(document, "health.get", "event frame");
    }

    [Test]
    [Arguments((int)StreamFrameProfile.NonNullableStringId)]
    [Arguments((int)StreamFrameProfile.NullableNumberId)]
    public async Task BindStream_Should_Refuse_An_Id_That_Is_Not_A_Nullable_String(int profile)
    {
        var document = await BindingTestHost.IngestAsync(new StreamOperationScenario(frameProfile: (StreamFrameProfile)profile));

        await AssertStreamRefusalAsync(document, "frame 'id' must be a nullable string");
    }

    [Test]
    public async Task BindStream_Should_Bind_A_Frame_Whose_Id_Is_Optional()
    {
        var document = await BindingTestHost.IngestAsync(new StreamOperationScenario(frameProfile: StreamFrameProfile.OptionalId));

        var plan = new BindingTestHost().Bind(
            document,
            Selection(StreamOperationScenario.OperationId),
            Curation(Groups(StreamOperationScenario.GroupName, RootGroup())));

        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();
        await Assert.That(operation.Stream).IsNotNull();
    }

    [Test]
    [Arguments((int)StreamFrameProfile.OptionalEvent)]
    [Arguments((int)StreamFrameProfile.OptionalData)]
    public async Task BindStream_Should_Refuse_A_Frame_Whose_Event_Or_Data_Is_Optional(int profile)
    {
        var document = await BindingTestHost.IngestAsync(new StreamOperationScenario(frameProfile: (StreamFrameProfile)profile));

        await AssertStreamRefusalAsync(document, "require 'event' and 'data'");
    }

    [Test]
    public async Task BindStream_Should_Refuse_An_Event_That_Is_Not_A_String()
    {
        var document = await BindingTestHost.IngestAsync(
            new StreamOperationScenario(frameProfile: StreamFrameProfile.NumberEvent));

        await AssertStreamRefusalAsync(document, "frame 'event' must be a string");
    }

    [Test]
    [Arguments((int)StreamExtensionProfile.MissingEncoding)]
    [Arguments((int)StreamExtensionProfile.UnsupportedEncoding)]
    public async Task BindStream_Should_Require_Sse_Encoding(int profile)
    {
        var document = await BindingTestHost.IngestAsync(
            new StreamOperationScenario(extensionProfile: (StreamExtensionProfile)profile));

        await AssertStreamRefusalAsync(document, "encoding' must equal 'sse");
    }

    [Test]
    public async Task BindStream_Should_Refuse_The_Ordinary_Message_Event_As_A_Failure()
    {
        var document = await BindingTestHost.IngestAsync(
            new StreamOperationScenario(extensionProfile: StreamExtensionProfile.MessageFailure));

        await AssertStreamRefusalAsync(document, "failureEvent' must not equal 'message");
    }

    [Test]
    public async Task BindStream_Should_Require_A_Cause_Schema()
    {
        var document = await BindingTestHost.IngestAsync(
            new StreamOperationScenario(extensionProfile: StreamExtensionProfile.MissingCauseSchema));

        await AssertStreamRefusalAsync(document, "causeSchema");
    }

    [Test]
    public async Task BindStream_Should_Require_A_Never_Error_Schema()
    {
        var document = await BindingTestHost.IngestAsync(
            new StreamOperationScenario(extensionProfile: StreamExtensionProfile.NonNeverErrorSchema));

        await AssertStreamRefusalAsync(document, "errorSchema");
    }

    [Test]
    public async Task BindStream_Should_Require_An_Error_Schema()
    {
        var document = await BindingTestHost.IngestAsync(
            new StreamOperationScenario(extensionProfile: StreamExtensionProfile.MissingErrorSchema));

        await AssertStreamRefusalAsync(document, "errorSchema");
    }

    [Test]
    public async Task BindStream_Should_Refuse_A_Request_Body()
    {
        var document = await BindingTestHost.IngestAsync(new StreamOperationScenario(carriesRequestBody: true));

        await AssertStreamRefusalAsync(document, "streaming operations must not carry a request body");
    }

    [Test]
    public async Task Bind_Should_Bind_An_Event_Stream_Into_A_Stream_Plan()
    {
        var document = await BindingTestHost.IngestAsync(new StreamOperationScenario());

        var plan = new BindingTestHost().Bind(
            document,
            Selection(StreamOperationScenario.OperationId),
            Curation(Groups(StreamOperationScenario.GroupName, RootGroup())));

        var operation = plan.Clients.SelectMany(static client => client.Operations).Single();
        await Assert.That(operation.Envelope).IsNull();
        await Assert.That(operation.Stream).IsNotNull();
        await Assert.That(operation.Stream!.PayloadTypeName).IsEqualTo("ExampleEvent");
        await Assert.That(operation.Stream.FailureEventName).IsEqualTo("effect/http-api/stream/failure");
        await Assert.That(operation.Stream.CauseTypeName).IsEqualTo("IStreamFailureCause[]");

        var cause = plan.Unions.Single(static union => union.Name == "IStreamFailureCause");
        await Assert.That(cause.Variants.Select(static variant => variant.Tag))
            .IsEquivalentTo(["Die", "Interrupt"], StringComparer.Ordinal, CollectionOrdering.Matching);
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert.That(cause.KnownImpossibleTags).IsEquivalentTo(["Fail"], CollectionOrdering.Any);
        await Assert.That(plan.Models.Select(static model => model.Name)).Contains("StreamFailureCauseDie");
        await Assert.That(plan.Models.Select(static model => model.Name)).Contains("StreamFailureCauseInterrupt");
        await Assert.That(plan.Models.Select(static model => model.Name)).DoesNotContain("StreamFailureCauseFail");
        await Assert.That(plan.Registry.TypeNames).Contains("IStreamFailureCause[]");
    }

    /// <summary>
    /// Upstream's codegen rule (<c>promiseWildcardInput</c>): the trailing wildcard becomes a
    /// required string input named <c>path</c>, first among the inputs, and a raw-byte success
    /// binds the binary envelope.
    /// </summary>
    [Test]
    public async Task Bind_Should_Carry_A_Trailing_Wildcard_As_A_Required_Path_Member()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("blob.read", path: "/api/blob/read/*", configure: operation => operation
                .Parameter("location", "query", QueryScenarioData.NullableLocationSelector, deepObject: true)
                .Response(200, "application/octet-stream", schema => schema.Type("string").Format("binary")))));

        var operation = new BindingTestHost()
            .Bind(document, Selection("blob.read"), Curation(Groups("blob", RootGroup())))
            .Clients.SelectMany(static client => client.Operations)
            .Single();

        var tail = operation.QueryRequest!.Properties[0];
        await Assert.That(tail.WireName).IsEqualTo("path");
        await Assert.That(tail.PropertyName).IsEqualTo("Path");
        await Assert.That(tail.Kind).IsEqualTo(QueryValueKind.RouteTail);
        await Assert.That(tail.IsRequired).IsTrue();
        await Assert.That(operation.QueryRequest.Properties[1].Kind).IsEqualTo(QueryValueKind.Location);
        await Assert.That(operation.Envelope!.Kind).IsEqualTo(EnvelopeKind.Binary);
        await Assert.That(operation.Envelope.PayloadName).IsEqualTo("Content");
        await Assert.That(operation.Envelope.PayloadType).IsTypeOf<BinaryTypeReferencePlan>();
    }

    [Test]
    public async Task Bind_Should_Name_The_Route_Tail_Wildcard_When_A_Path_Query_Exists()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("blob.read", path: "/api/blob/read/*", configure: operation => operation
                .Parameter("path", "query", schema => schema.Type("string"))
                .Response(200, "application/octet-stream", schema => schema.Type("string").Format("binary")))));

        var operation = new BindingTestHost()
            .Bind(document, Selection("blob.read"), Curation(Groups("blob", RootGroup()), stringQueryParameters: [StringQueryParameter("blob.read", "path")]))
            .Clients.SelectMany(static client => client.Operations)
            .Single();

        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert
            .That(operation.QueryRequest!.Properties.Select(static property => $"{property.PropertyName}:{property.Kind}"))
            .IsEquivalentTo(["Wildcard:RouteTail", "Path:Text"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Bind_Should_Refuse_An_Octet_Stream_Success_That_Is_Not_Binary()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("health.get", configure: operation => operation
                .Response(200, "application/octet-stream", schema => schema.Type("string")))));

        await AssertOperationRefusalAsync(document, "health.get", "the success response must carry a JSON schema");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Reserved_Parameter_Name()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.item", path: "/api/widget/{request}", configure: operation => operation
                .Parameter("request", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var exception = RefuseWidgetItem(document);

        await AssertProblemAsync(exception, BindingErrorCategory.Naming, "reserved by the emitted method signature");
    }

    [Test]
    public async Task Bind_Should_Admit_A_Path_Parameter_Named_Options()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.item", path: "/api/widget/{options}", configure: operation => operation
                .Parameter("options", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))));

        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.item"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null))));

        var item = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(item.Parameters.Single().Name).IsEqualTo("options");
    }

    /// <summary>A parameterNames row can rename a route value, so a keyword route value is a missing row; a header has no row.</summary>
    [Test]
    [Arguments("path", "class")]
    [Arguments("path", "event")]
    [Arguments("header", "class")]
    [Arguments("handle", "class")]
    public async Task Bind_Should_Refuse_A_Parameter_Named_By_A_CSharp_Keyword(string location, string keyword)
    {
        BindingException exception;
        BindingError expected;
        if (location == "header")
        {
            var document = await BindingTestHost.IngestAsync(PtyScenario(headerName: keyword));
            exception = Assert.Throws<BindingException>(() => _ = BindPtys(document, EmissionMode.InternalRaw));
            expected = HeaderKeywordRefusal("pty.connect.token", keyword);
        }
        else
        {
            var handle = location == "handle";
            var operationId = handle ? "alias.list" : "widget.item";
            var document = await BindingTestHost.IngestAsync(WidgetRouteScenario(operationId, keyword));
            exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection(operationId),
                Curation(Groups(handle ? "alias" : "widget",
                    ClientGroup(handle ? "Aliases" : "Widgets", handle ? "AliasClient" : null, handle ? keyword : null)))));
            expected = PathKeywordRefusal(operationId, keyword);
        }

        await Assert.That(exception.Errors).IsEquivalentTo([expected], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("class", "classId", "classId")]
    [Arguments("value", null, "value")]
    public async Task Bind_Should_Name_A_Path_Parameter_From_Its_Row_Or_Mechanical_Name(string parameter, string? curatedName, string expectedName)
    {
        var document = await BindingTestHost.IngestAsync(WidgetRouteScenario("widget.item", parameter));

        var plan = new BindingTestHost().Bind(document, Selection("widget.item"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)),
                parameterNames: curatedName is null ? [] : [ParameterName("widget.item", parameter, curatedName)]));

        var item = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(item.Parameters.Single().Name).IsEqualTo(expectedName);
    }

    /// <summary><c>content</c> is reserved on every emitted operation signature, like <c>request</c>.</summary>
    [Test]
    public async Task Bind_Should_Refuse_A_Reserved_Content_Route_Value()
    {
        var document = await BindingTestHost.IngestAsync(WidgetRouteScenario("widget.create", "content", binaryBody: true));

        var exception = Assert.Throws<BindingException>(() => _ = BindWidgets(document, "widget.create"));

        await Assert.That(exception.Errors).IsEquivalentTo([ReservedParameterRefusal("widget.create", "content")], CollectionOrdering.Matching);
    }

    private static SpecScenario WidgetRouteScenario(string operationId, string parameter, bool binaryBody = false) =>
        SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", static schema => schema
                .Type("object")
                .Property("id", static property => property.Type("string"), required: true))
            .WithOperation(operationId, method: binaryBody ? "post" : "get", path: $"/api/widget/{{{parameter}}}", configure: operation =>
            {
                _ = operation.Parameter(parameter, "path", static schema => schema.Type("string"), required: true)
                    .Response(200, "application/json", schema => _ = operationId == "alias.list"
                        ? schema.Type("object").Property("items", static property => property.Type("array").Items(static item => item.Type("string")), required: true)
                        : schema.Ref("ItemInfo"));
                if (binaryBody)
                {
                    _ = operation.RequestBody("application/octet-stream", static schema => schema.Type("string").Format("binary"), required: true);
                }
            }));

    /// <summary>A route keyword has a curation remedy, so its refusal identifies the missing row.</summary>
    private static BindingError PathKeywordRefusal(string operationId, string parameter) =>
        new(BindingErrorCategory.Curation, $"{operationId}:{parameter}",
            $"route value '{parameter}' is a C# keyword: add a parameterNames row that names it")
        { Kind = BindingErrorKind.MissingRow };

    private static BindingError HeaderKeywordRefusal(string operationId, string header) =>
        new(BindingErrorCategory.Naming, operationId, $"header parameter '{header}' is a C# keyword, and no curation row renames a header");

    /// <summary>Builds a naming refusal for a route value that uses a reserved signature parameter name.</summary>
    private static BindingError ReservedParameterRefusal(string operationId, string parameter) =>
        new(BindingErrorCategory.Naming, operationId, $"parameter name '{parameter}' is reserved by the emitted method signature");

    [Test]
    public async Task Bind_Should_Refuse_An_Operation_Declaring_Both_A_Body_And_Query_Parameters()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.create", method: "post", path: "/api/widget", configure: operation => operation
                .Parameter("dryRun", "query", QueryScenarioData.NullableString)
                .RequestBody("application/json", body => body
                    .Type("object")
                    .Property("title", property => property.AnyOf(
                        static branch => branch.Type("string"),
                        static branch => branch.Type("null"))), required: true)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo")))));

        await AssertWidgetRefusalAsync(document, "request body and query", "widget.create", strings: ["dryRun"]);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Response_Type_Shadowing_A_Model()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetStateResponse", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.state", path: "/api/widget-state", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetStateResponse")))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("widget.state"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)))));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
                                                       && error.Problem.Contains("WidgetStateResponse", StringComparison.Ordinal)
                                                       && error.Problem.Contains("response type", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Client_Name_Colliding_With_The_Spine()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("gadget.part"),
            Curation(Groups("gadget", ClientGroup(clientName: "Gadgets", handleName: "ListCursor", handleParameter: "gadgetID")))));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
            && error.Problem.Contains("ListCursor", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_Colliding_Method_Names_On_A_Client()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario(spec => spec
            .WithOperation("gadget.part.get", path: "/api/gadget/{gadgetID}/part-alias/{partID}", configure: operation => operation
                .Parameter("gadgetID", "path", schema => schema.Type("string"), required: true)
                .Parameter("partID", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema
                    .Type("object")
                    .Property("data", property => property.Ref("GadgetPart"), required: true)))));

        var exception = RefuseGadgetPartNames(document);

        await AssertProblemAsync(exception, BindingErrorCategory.Naming, "GetPartAsync");
        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
            && error.Problem.Contains("GadgetPartResponse", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_Colliding_Route_Members_In_A_Container()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario(spec => spec
            .WithOperation("gadget.part.get", path: "/api/gadget-part", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("GadgetPart")))));

        var exception = RefuseGadgetPartNames(document);

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
                                                       && error.Problem.Contains("route member", StringComparison.Ordinal)
                                                       && error.Problem.Contains("GetPart", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_Colliding_Client_Type_Names()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("gadget.part"),
            Curation(Groups("gadget", ClientGroup(clientName: "Gadget", handleName: "GadgetClient", handleParameter: "gadgetID")))));

        await Assert.That(exception.Errors).Contains(static error => error.Category == BindingErrorCategory.Naming
            && error.Problem.Contains("GadgetClient", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("data", "Entries", "envelopePayloadNames row for 'widget.entry.list' repeats the list naming rule's derived value 'Entries'")]
    [Arguments("items", "Items", "envelopePayloadNames row for 'widget.entry.list' repeats the list naming rule's derived value 'Items'")]
    public async Task Bind_Should_Refuse_A_Redundant_List_Payload_Row(string wireName, string payloadName, string problem)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("widget.entry.list", configure: operation => operation
                .Response(200, "application/json", schema => schema.Type("object")
                    .Property(wireName, property => property.Type("array").Items(item => item.Type("string")), required: true)))));
        var curation = Curation(
            Groups("widget", ClientGroup("Widgets", handleName: null, handleParameter: null)),
            envelopePayloadNames: [EnvelopePayloadName("widget.entry.list", payloadName)]);

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(document, Selection("widget.entry.list"), curation));

        var error = exception.Errors.Single();
        await Assert.That(error.Category).IsEqualTo(BindingErrorCategory.Curation);
        await Assert.That(error.Subject).IsEqualTo("widget.entry.list");
        await Assert.That(error.Problem).IsEqualTo(problem);
    }

    [Test]
    public async Task Bind_Should_Apply_A_List_Payload_Override_That_Differs_From_The_Single_Key_Name()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation("widget.entry.list", configure: operation => operation
                .Response(200, "application/json", schema => schema.Type("object")
                    .Property("items", property => property.Type("array").Items(item => item.Type("string")), required: true)))));
        var curation = Curation(
            Groups("widget", ClientGroup("Widgets", handleName: null, handleParameter: null)),
            envelopePayloadNames: [EnvelopePayloadName("widget.entry.list", "Entries")]);

        var plan = new BindingTestHost().Bind(document, Selection("widget.entry.list"), curation);

        var operation = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(operation.Envelope!.PayloadName).IsEqualTo("Entries");
        await Assert.That(operation.Envelope.WireMemberName).IsEqualTo("items");
        await Assert.That(operation.Envelope.PayloadType).IsTypeOf<ListTypeReferencePlan>();
    }

    [Test]
    public async Task Bind_Should_Apply_A_Curated_Payload_Name_Override()
    {
        var document = await BindingTestHost.IngestAsync(GadgetScenario());
        EnvelopePayloadNameCuration[] payloadNames = [EnvelopePayloadName("gadget.part", "Component")];

        var plan = new BindingTestHost().Bind(
            document,
            Selection("gadget.part"),
            Curation(
                Groups("gadget", ClientGroup(clientName: "Gadgets", handleName: "GadgetClient", handleParameter: "gadgetID")),
                envelopePayloadNames: payloadNames));

        var part = plan.Clients.Single(static client => client.Role == ClientRole.Handle).Operations.Single();
        await Assert.That(part.Envelope!.PayloadName).IsEqualTo("Component");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Payload_Name_Colliding_With_The_Response_Spine()
    {
        var document = await BindingTestHost.IngestAsync(StatusScenario());

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("widget.status"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)))));

        await Assert
            .That(exception.Errors.Single(static error => error.Category == BindingErrorCategory.Naming).Problem)
            .Contains("Status");
    }

    [Test]
    [Arguments("EqualityContract", "payload name 'EqualityContract' collides with the response spine of 'WidgetStatusResponse'")]
    [Arguments("ToString", "payload name 'ToString' collides with the response spine of 'WidgetStatusResponse'")]
    [Arguments("GetHashCode", "payload name 'GetHashCode' collides with the response spine of 'WidgetStatusResponse'")]
    [Arguments("PrintMembers", "payload name 'PrintMembers' collides with the response spine of 'WidgetStatusResponse'")]
    public async Task Bind_Should_Refuse_A_Payload_Name_Colliding_With_Record_Members(string payloadName, string problem)
    {
        var document = await BindingTestHost.IngestAsync(StatusScenario());
        var curation = Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)),
            envelopePayloadNames: [EnvelopePayloadName("widget.status", payloadName)]);
        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document, Selection("widget.status"), curation));

        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Naming, "widget.status", problem));
    }

    [Test]
    public async Task Bind_Should_Accept_An_Override_That_Resolves_A_Reserved_Payload_Collision()
    {
        var document = await BindingTestHost.IngestAsync(StatusScenario());
        var plan = new BindingTestHost().Bind(
            document,
            Selection("widget.status"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)),
                envelopePayloadNames: [EnvelopePayloadName("widget.status", "WidgetStatus")]));

        var status = plan.Clients.Single(static client => client.Role == ClientRole.Collection).Operations.Single();
        await Assert.That(status.Envelope!.PayloadName).IsEqualTo("WidgetStatus");
    }

    [Test]
    public async Task Bind_Should_Report_Failures_For_Every_Selected_Operation()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("ItemInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("health.get", configure: operation => operation
                .Parameter("limit", "query", schema => schema.Type("string").Format("uri"))
                .Response(200, "application/json", schema => schema.Ref("ItemInfo")))
            .WithOperation("health.probe", path: "/api/health-probe", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("ItemInfo"))
                .Response(302, "application/json", schema => schema.Ref("ItemInfo")))));

        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection("health.get", "health.probe"),
            Curation(Groups("health", RootGroup()))));

        var subjects = exception
            .Errors
            .Where(static error => error.Category == BindingErrorCategory.Operation)
            .Select(static error => error.Subject)
            .ToArray();
        await Assert.That(subjects).Contains("health.get");
        await Assert.That(subjects).Contains("health.probe");
    }

    private static async Task AssertOperationRefusalAsync(SpecDocument document, string operationId, string expectedProblem,
        string groupName = "health")
    {
        var exception = Assert.Throws<BindingException>(() => _ = new BindingTestHost().Bind(
            document,
            Selection(operationId),
            Curation(Groups(groupName, RootGroup()))));

        await AssertOperationProblemAsync(exception, operationId, expectedProblem);
    }

    private static Task AssertStreamRefusalAsync(SpecDocument document, string expectedProblem) =>
        AssertOperationRefusalAsync(
            document,
            StreamOperationScenario.OperationId,
            expectedProblem,
            StreamOperationScenario.GroupName);

    private static SpecScenario WidgetListScenario(Action<OperationBuilder> configureParameters,
        Action<SpecDocumentBuilder>? configureSchemas = null) =>
        SpecScenario.Define(spec =>
        {
            configureSchemas?.Invoke(spec);
            _ = spec
                .WithSchema("WidgetInfo", schema => schema
                    .Type("object")
                    .Property("id", property => property.Type("string"), required: true))
                .WithOperation("widget.list", path: "/api/widget", configure: operation =>
                {
                    configureParameters(operation);
                    _ = operation.Response(200, "application/json", schema => schema.Ref("WidgetInfo"));
                });
        });

    private static SpecScenario WidgetCreateScenario(Action<SchemaBuilder> configureBody,
        string mediaType = "application/json", bool required = true) =>
        SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.create", method: "post", path: "/api/widget", configure: operation => operation
                .RequestBody(mediaType, configureBody, required)
                .Response(200, "application/json", schema => schema.Ref("WidgetInfo"))));

    private static SpecScenario DataLocationScenario(Action<SchemaBuilder>? data = null,
        Action<SchemaBuilder>? location = null, bool locationRequired = true) =>
        SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("PlaceInfo", schema => schema
                .Type("object")
                .Property("directory", property => property.Type("string"), required: true))
            .WithSchema("WidgetEnvelope", schema => schema
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("location", location ?? (static property => property.Ref("PlaceInfo")), required: locationRequired)
                .Property("data", data ?? (static property => property.Ref("WidgetInfo")), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetEnvelope"))));

    /// <summary>
    /// A bare success whose body is an inline object: ingestion promotes it into the graph
    /// under the operation-scoped key, and the payload is that promoted schema itself.
    /// </summary>
    private static SpecScenario InlinePayloadScenario(Action<SchemaBuilder> configurePayload) =>
        SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithOperation("widget.stats", path: "/api/widget/stats", configure: operation => operation
                .Response(200, "application/json", schema =>
                {
                    _ = schema.Type("object").AdditionalPropertiesFalse();
                    configurePayload(schema);
                })));

    private static SpecScenario NoContentScenario(Action<OperationBuilder>? configure = null) =>
        SpecScenario.Define(spec => spec
            .WithOperation("widget.create", method: "post", path: "/api/widget", configure: operation =>
            {
                _ = operation
                    .RequestBody("application/json", schema => schema
                        .Type("object")
                        .Property("title", property => property.Type("string"), required: true), required: true)
                    .WithoutResponse(200)
                    .Response(204);
                configure?.Invoke(operation);
            }));

    /// <summary>
    /// Two wire groups whose handle-scoped operations merge into one curated client family.
    /// The later-sorting group optionally declares a header, so a family whose emission is
    /// taken from the earlier-sorting row can be caught carrying it.
    /// </summary>
    private static SpecScenario MergedGadgetScenario(bool gizmoDeclaresHeader = false) =>
        GadgetScenario(spec => spec
            .WithOperation("gizmo.list", path: "/api/gadget/{gadgetID}/gizmo", configure: operation =>
            {
                _ = operation.Parameter("gadgetID", "path", static schema => schema.Type("string"), required: true);
                if (gizmoDeclaresHeader)
                {
                    _ = operation.Parameter("x-opencode-ticket", "header", QueryScenarioData.NullableString);
                }

                _ = operation.Response(200, "application/json", static schema => schema
                    .Type("object")
                    .Property("data", static property => property.Ref("GadgetPart"), required: true));
            }));

    /// <summary>The header-bearing family shape: a handle group whose connect-token call declares a wire header.</summary>
    private static Task<SpecDocument> ListDocumentAsync(string operationId, string wireName) =>
        BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation(operationId, configure: operation => operation
                .Response(200, "application/json", schema => schema.Type("object")
                    .Property(wireName, static property => property.Type("array").Items(static item => item.Type("string")), required: true)))));

    private static GenerationCuration ListCuration(string operationId, string rows)
    {
        var curatedMethod = operationId == "credential.settings.list" ? "ListSettingsAsync" : "ListChangesAsync";
        return Curation(Groups(operationId.Split('.')[0], ClientGroup("Examples", handleName: null, handleParameter: null)),
            operationNames: rows == "none" ? [] : [OperationName(operationId, curatedMethod)],
            envelopePayloadNames: rows == "both" ? [EnvelopePayloadName(operationId, "Changes")] : []);
    }

    private static SpecScenario PtyScenario(bool headerRequired = false, string headerName = "x-opencode-ticket") =>
        SpecScenario.Define(spec => spec
            .WithSchema("PtyInfo", schema => schema
                .Type("object")
                .Property("id", property => property.Type("string"), required: true))
            .WithSchema("PtyTicket", schema => schema
                .Type("object")
                .Property("token", property => property.Type("string"), required: true))
            .WithOperation("pty.get", path: "/api/pty/{ptyID}", configure: operation => operation
                .Parameter("ptyID", "path", schema => schema.Type("string"), required: true)
                .Response(200, "application/json", schema => schema.Ref("PtyInfo")))
            .WithOperation("pty.connect.token", method: "post", path: "/api/pty/{ptyID}/connect-token",
                configure: operation => operation
                    .Parameter("ptyID", "path", schema => schema.Type("string"), required: true)
                    .Parameter(
                        headerName,
                        "header",
                        headerRequired ? static schema => schema.Type("string") : QueryScenarioData.NullableString,
                        required: headerRequired)
                    .Response(200, "application/json", schema => schema.Ref("PtyTicket"))));

    /// <summary>The connect-token call closes with no grammar verb, so the family's row names it, as the pin's does.</summary>
    private static EmitPlan BindPtys(SpecDocument document, EmissionMode emission) =>
        new BindingTestHost().Bind(
            document,
            Selection("pty.get", "pty.connect.token"),
            Curation(
                Groups("pty", ClientGroup(clientName: "Ptys", handleName: "PtyClient", handleParameter: "ptyID", emission: emission)),
                operationNames: [OperationName("pty.connect.token", "CreateConnectTokenAsync")]));

    private static EmitPlan BindWidgets(SpecDocument document, string operationId = "widget.list",
        IReadOnlyList<string>? integers = null, IReadOnlyList<string>? strings = null) =>
        new BindingTestHost().Bind(
            document,
            Selection(operationId),
            Curation(
                Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)),
                queryParameterTypes: [.. (integers ?? []).Select(parameter => QueryParameterType(operationId, parameter, "int"))],
                stringQueryParameters: [.. (strings ?? []).Select(parameter => StringQueryParameter(operationId, parameter))]));

    private static async Task AssertWidgetRefusalAsync(SpecDocument document, string expectedProblem,
        string operationId = "widget.list", IReadOnlyList<string>? strings = null)
    {
        var exception = Assert.Throws<BindingException>(() => _ = BindWidgets(document, operationId, strings: strings));

        await AssertOperationProblemAsync(exception, operationId, expectedProblem);
    }

    private static class QueryScenarioData
    {
        public static void NullableString(SchemaBuilder schema) => schema.AnyOf(
            static branch => branch.Type("string"),
            static branch => branch.Type("null"));

        public static void NullableOrderEnum(SchemaBuilder schema) => schema.AnyOf(
            static branch => branch.Type("string").Enum("asc", "desc"),
            static branch => branch.Type("null"));

        public static void NullableParentFilter(SchemaBuilder schema) => schema.AnyOf(
            static branch => branch.AnyOf(
                static inner => inner.Type("string").AllOf(static constraint => constraint.Raw("pattern", "\"^wid\"")),
                static inner => inner.Type("string").Enum("null")),
            static branch => branch.Type("null"));

        public static void NullableFormattedParentFilter(SchemaBuilder schema) => schema.AnyOf(
            static branch => branch.AnyOf(
                static inner => inner
                    .Type("string")
                    .Format("uri")
                    .AllOf(static constraint => constraint.Raw("pattern", "\"^wid\"")),
                static inner => inner.Type("string").Enum("null")),
            static branch => branch.Type("null"));

        public static void NullableLocationSelector(SchemaBuilder schema) => schema.AnyOf(
            static branch => branch
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("directory", static property => property.AnyOf(
                    static inner => inner.Type("string"),
                    static inner => inner.Type("null"))),
            static branch => branch.Type("null"));

        public static void NullableFormattedLocationSelector(SchemaBuilder schema) => schema.AnyOf(
            static branch => branch
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("directory", static property => property.AnyOf(
                    static inner => inner.Type("string").Format("uri"),
                    static inner => inner.Type("null"))),
            static branch => branch.Type("null"));

        public static void NullableSelectorWithExtraMember(SchemaBuilder schema) => schema.AnyOf(
            static branch => branch
                .Type("object")
                .AdditionalPropertiesFalse()
                .Property("directory", static property => property.AnyOf(
                    static inner => inner.Type("string"),
                    static inner => inner.Type("null")))
                .Property("workspace", static property => property.AnyOf(
                    static inner => inner.Type("string"),
                    static inner => inner.Type("null")))
                .Property("project", static property => property.AnyOf(
                    static inner => inner.Type("string"),
                    static inner => inner.Type("null"))),
            static branch => branch.Type("null"));
    }

    private static SpecScenario GadgetScenario(Action<SpecDocumentBuilder>? extend = null, bool parametersReversed = false) =>
        SpecScenario.Define(spec =>
        {
            _ = spec
                .WithSchema("GadgetPart", schema => schema
                    .Type("object")
                    .Property("id", property => property.Type("string"), required: true))
                .WithSchema("GadgetMissingError", schema => schema
                    .Type("object")
                    .Property("_tag", property => property.Type("string").Enum("GadgetMissingError"), required: true)
                    .Property("message", property => property.Type("string"), required: true))
                .WithOperation("gadget.part", path: "/api/gadget/{gadgetID}/part/{partID}", configure: operation =>
                {
                    if (parametersReversed)
                    {
                        _ = operation
                            .Parameter("partID", "path", schema => schema.Type("string"), required: true)
                            .Parameter("gadgetID", "path", schema => schema.Type("string"), required: true);
                    }
                    else
                    {
                        _ = operation
                            .Parameter("gadgetID", "path", schema => schema.Type("string"), required: true)
                            .Parameter("partID", "path", schema => schema.Type("string"), required: true);
                    }

                    _ = operation
                        .Summary("Get gadget part")
                        .Response(200, "application/json", schema => schema
                            .Type("object")
                            .Property("data", property => property.Ref("GadgetPart"), required: true))
                        .Response(404, "application/json", schema => schema.Ref("GadgetMissingError"));
                });
            extend?.Invoke(spec);
        });

    private static SpecScenario StatusScenario() =>
        SpecScenario.Define(spec => spec
            .WithSchema("WidgetState", schema => schema
                .Type("object")
                .Property("value", property => property.Type("string"), required: true))
            .WithOperation("widget.status", path: "/api/widget-status", configure: operation => operation
                .Response(200, "application/json", schema => schema.Ref("WidgetState"))));
}
