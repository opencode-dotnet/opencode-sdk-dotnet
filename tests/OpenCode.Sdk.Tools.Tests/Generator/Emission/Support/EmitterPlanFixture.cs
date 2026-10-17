using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal static class EmitterPlanFixture
{
    public const string ValueSummary = "/// Gets the value.";

    public static async Task<EmitPlan> CreateStreamPlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(new StreamOperationScenario());
        return new BindingTestHost().Bind(
            document,
            Selection(StreamOperationScenario.OperationId),
            Curation(Groups(StreamOperationScenario.GroupName, RootGroup())));
    }

    public static async Task<EmitPlan> CreateStructuralUnionPlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(new StructuralUnionScenario());
        return new BindingTestHost().Bind(
            document,
            Selection(StructuralUnionScenario.OperationId),
            Curation(Groups(StructuralUnionScenario.GroupName, RootGroup())));
    }

    public static async Task<EmitPlan> CreateFirstMatchUnionPlanAsync()
    {
        var document = await BindingTestHost.IngestAsync(new FirstMatchUnionScenario());
        return new BindingTestHost().Bind(
            document,
            Selection(FirstMatchUnionScenario.OperationId),
            Curation(Groups(FirstMatchUnionScenario.GroupName, RootGroup())));
    }

    public static EmitPlan Create()
    {
        var models = new ModelPlan[]
        {
            CreateExampleItem(), CreateExamplePlace(), CreateExampleMode(),
            CreateVariant("CreatedEvent", "created", "item", "Item", Named("ExampleItem")),
            CreateVariant("DeletedEvent", "deleted", "id", "ID", Named("string")),
            CreateRpcEvent(), CreateError(), CreateWidgetCreateRequest(), CreateOpenSettings(),
        };
        var unions = new[] { CreateExampleEvent(), CreateOpenCodeError(), };

        var plan = new EmitPlan
        {
            ImplicitAliases = StabilizeDuplicateCollapse.Empty,
            SelectedOperationIds = ["example.get"],
            Models = models,
            Unions = unions,
            Clients = CreateClientPlans(),
            Registry = new RegistryPlan
            {
                TypeNames =
                [
                    "BadRequestError",
                    "CreatedEvent",
                    "DeletedEvent",
                    "IExampleEvent",
                    "ExampleItem",
                    "ExampleMode",
                    "ExamplePlace",
                    "IOpenCodeError",
                    "RpcEvent",
                    "UnknownExampleEvent",
                    "UnknownOpenCodeError",
                    "WidgetCreateRequest",
                    "WidgetCreateResponseEnvelope",
                    "WidgetItemListResponseEnvelope",
                    "WidgetItemResponseEnvelope",
                    "WidgetListResponseEnvelope",
                    "WidgetSpotListResponseEnvelope",
                    "WidgetSpotResponseEnvelope",
                ],
            },
            PendingOperations = [],
            DeclinedOperations = [],
            TransportOwnedOperationIds = [],
        }.WithSingleSlice();
        return WireDocumentationPolicy.Apply(plan);
    }

    public static IReadOnlyList<ClientPlan> CreateClientPlans() =>
    [
        new ClientPlan
        {
            Name = "OpenCodeClient",
            Namespace = "OpenCode.Sdk",
            Role = ClientRole.Root,
            SubClients =
            [
                new ClientReferencePlan
                {
                    PropertyName = "Widgets",
                    TypeName = "WidgetsClient",
                },
            ],
            Operations = [CreatePingOperation()],
        },
        new ClientPlan
        {
            Name = "WidgetClient",
            Namespace = "OpenCode.Sdk",
            Role = ClientRole.Handle,
            ContainerName = "Widgets",
            SubClients = [],
            HandleParameter = CreateWidgetParameter(),
            Operations = [CreateItemOperation(), CreateItemListOperation()],
        },
        new ClientPlan
        {
            Name = "WidgetsClient",
            Namespace = "OpenCode.Sdk",
            Role = ClientRole.Collection,
            ContainerName = "Widgets",
            SubClients = [],
            HandleFactory = new HandleFactoryPlan
            {
                MethodName = "GetWidgetClient",
                HandleTypeName = "WidgetClient",
                Parameter = CreateWidgetParameter(),
            },
            Operations =
            [
                CreateOverviewOperation(),
                CreateWidgetListOperation(),
                CreateWidgetCreateOperation(),
                CreateWidgetRemoveOperation(),
                CreateWidgetSpotOperation(),
                CreateWidgetSpotListOperation(),
            ],
        },
    ];

    /// <summary>
    /// The internal-raw family shape (ADR-0021): the generated layer emits internal while the
    /// root client keeps the public accessor its hand-written family door will answer.
    /// </summary>
    public static IReadOnlyList<ClientPlan> CreateInternalRawClientPlans() =>
    [
        new ClientPlan
        {
            Name = "OpenCodeClient",
            Namespace = "OpenCode.Sdk",
            Role = ClientRole.Root,
            SubClients =
            [
                new ClientReferencePlan
                {
                    PropertyName = "Ptys",
                    TypeName = "PtysClient",
                },
            ],
            Operations = [],
        },
        new ClientPlan
        {
            Name = "PtyRawClient",
            Namespace = "OpenCode.Sdk.Internal",
            Role = ClientRole.Handle,
            ContainerName = "Ptys",
            Emission = EmissionMode.InternalRaw,
            SubClients = [],
            HandleParameter = CreatePtyParameter(),
            Operations = [CreatePtyConnectTokenOperation()],
        },
        new ClientPlan
        {
            Name = "PtysRawClient",
            Namespace = "OpenCode.Sdk.Internal",
            Role = ClientRole.Collection,
            ContainerName = "Ptys",
            Emission = EmissionMode.InternalRaw,
            SubClients = [],
            HandleFactory = new HandleFactoryPlan
            {
                MethodName = "GetPtyRawClient",
                HandleTypeName = "PtyRawClient",
                Parameter = CreatePtyParameter(),
            },
            Operations = [CreatePtyListOperation()],
        },
    ];

    private static OperationParameterPlan CreatePtyParameter() =>
        new()
        {
            WireName = "ptyID",
            Name = "ptyId",
            TypeName = "string",
            IsHandleParameter = true,
        };

    private static OperationPlan CreatePtyConnectTokenOperation() =>
        new()
        {
            OperationId = "pty.connect.token",
            MethodName = "CreateConnectTokenAsync",
            HttpMethod = "post",
            RouteTemplate = "/api/pty/{ptyID}/connect-token",
            RouteContainerName = "Ptys",
            RouteMemberName = "CreateConnectToken",
            Parameters = [CreatePtyParameter()],
            DeclaredHeaders =
            [
                new DeclaredHeaderPlan
                {
                    WireName = "x-opencode-ticket",
                    Name = "xOpencodeTicket",
                },
            ],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "PtyConnectTokenResponse",
                AdapterTypeName = "PtyConnectTokenResponseAdapter",
                PayloadName = "ConnectToken",
                PayloadType = Named("ExampleItem"),
                Kind = EnvelopeKind.Bare,
                SuccessStatusCode = 200,
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses = [],
            },
            Summary = "Mint one pty connect token",
            Description = null,
        };

    private static OperationPlan CreatePtyListOperation() =>
        new()
        {
            OperationId = "pty.list",
            MethodName = "ListPtysAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/pty",
            RouteContainerName = "Ptys",
            RouteMemberName = "ListPtys",
            Parameters = [],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "PtyListResponse",
                AdapterTypeName = "PtyListResponseAdapter",
                PayloadName = "Ptys",
                PayloadType = Named("ExampleItem"),
                Kind = EnvelopeKind.Bare,
                SuccessStatusCode = 200,
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses = [],
            },
            Summary = "List the ptys",
            Description = null,
        };

    private static OperationPlan CreatePingOperation() =>
        new()
        {
            OperationId = "ping.get",
            MethodName = "GetPingAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/ping",
            RouteContainerName = "Ping",
            RouteMemberName = "Get",
            Parameters = [],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "PingResponse",
                AdapterTypeName = "PingResponseAdapter",
                PayloadName = "Ping",
                PayloadType = Named("ExampleItem"),
                Kind = EnvelopeKind.Bare,
                SuccessStatusCode = 200,
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses =
                [
                    new ErrorStatusPlan
                    {
                        StatusCode = 400,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "BadRequestError",
                                TypeName = "BadRequestError",
                            }
                        ],
                    },
                ],
            },
            Summary = "Check example ping",
            Description = "Reports the example service state.",
        };

    private static OperationPlan CreateOverviewOperation() =>
        new()
        {
            OperationId = "widget.overview.get",
            MethodName = "GetOverviewAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/widget-overview",
            RouteContainerName = "Widgets",
            RouteMemberName = "GetOverview",
            Parameters = [],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetOverviewResponse",
                AdapterTypeName = "WidgetOverviewResponseAdapter",
                PayloadName = "Overview",
                PayloadType = Named("ExampleItem"),
                Kind = EnvelopeKind.Bare,
                SuccessStatusCode = 200,
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses = [],
            },
            Summary = "List the widget overview",
            Description = null,
        };

    private static OperationPlan CreateItemOperation() =>
        new()
        {
            OperationId = "widget.item.get",
            MethodName = "GetItemAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/widget/{widgetID}/item/{itemID}",
            RouteContainerName = "Widgets",
            RouteMemberName = "GetItem",
            Parameters =
            [
                CreateWidgetParameter(),
                new OperationParameterPlan
                {
                    WireName = "itemID",
                    Name = "itemId",
                    TypeName = "string",
                    IsHandleParameter = false,
                },
            ],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetItemResponse",
                AdapterTypeName = "WidgetItemResponseAdapter",
                PayloadName = "Item",
                PayloadType = Named("ExampleItem"),
                Kind = EnvelopeKind.Data,
                SuccessStatusCode = 200,
                EnvelopeDtoTypeName = "WidgetItemResponseEnvelope",
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses =
                [
                    new ErrorStatusPlan
                    {
                        StatusCode = 400,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "BadRequestError",
                                TypeName = "BadRequestError",
                            }
                        ],
                    },
                    new ErrorStatusPlan
                    {
                        StatusCode = 404,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "GoneError",
                                TypeName = "GoneError",
                            },
                            new ErrorTagPlan
                            {
                                Tag = "MissingError",
                                TypeName = "MissingError",
                            },
                        ],
                    },
                ],
            },
            Summary = "Get one widget item",
            Description = "Retrieves one item owned by the widget.",
        };

    private static OperationParameterPlan CreateWidgetParameter() =>
        new()
        {
            WireName = "widgetID",
            Name = "widgetId",
            TypeName = "string",
            IsHandleParameter = true,
        };

    /// <summary>A flat query-request list operation covering every query value kind, a cursor envelope, and a 5xx arm.</summary>
    private static OperationPlan CreateWidgetListOperation() =>
        new()
        {
            OperationId = "widget.list",
            MethodName = "ListWidgetsAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/widget",
            RouteContainerName = "Widgets",
            RouteMemberName = "ListWidgets",
            Parameters = [],
            QueryRequest = new QueryRequestPlan
            {
                TypeName = "WidgetListRequest",
                DerivesFromListRequest = false,
                Properties =
                [
                    QueryProperty("query", "Query", QueryValueKind.Text, isRequired: true),
                    QueryProperty("mode", "Mode", QueryValueKind.Enum, isRequired: true, enumTypeName: "ExampleMode"),
                    QueryProperty("fallbackMode", "FallbackMode", QueryValueKind.Enum, enumTypeName: "ExampleMode"),
                    QueryProperty("limit", "Limit", QueryValueKind.Int32, description: "Maximum widget count."),
                    QueryProperty("order", "Order", QueryValueKind.ListOrder),
                    QueryProperty("cursor", "Cursor", QueryValueKind.Text),
                    QueryProperty("enabled", "Enabled", QueryValueKind.BooleanText),
                    QueryProperty("parentID", "ParentId", QueryValueKind.SessionParentFilter),
                    QueryProperty("location", "Location", QueryValueKind.Location),
                ],
            },
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetListResponse",
                AdapterTypeName = "WidgetListResponseAdapter",
                PayloadName = "Widgets",
                PayloadType = ListOf(Named("ExampleItem")),
                Kind = EnvelopeKind.CursorList,
                SuccessStatusCode = 200,
                EnvelopeDtoTypeName = "WidgetListResponseEnvelope",
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses =
                [
                    new ErrorStatusPlan
                    {
                        StatusCode = 400,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "BadRequestError",
                                TypeName = "BadRequestError",
                            }
                        ],
                    },
                    new ErrorStatusPlan
                    {
                        StatusCode = 500,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "BadRequestError",
                                TypeName = "BadRequestError",
                            }
                        ],
                    },
                ],
            },
            Summary = "List the widgets",
            Description = null,
        };

    /// <summary>A handle-scoped list whose query request derives from the ListRequest base.</summary>
    private static OperationPlan CreateItemListOperation() =>
        new()
        {
            OperationId = "widget.item.list",
            MethodName = "ListItemsAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/widget/{widgetID}/item",
            RouteContainerName = "Widgets",
            RouteMemberName = "ListItems",
            Parameters = [CreateWidgetParameter()],
            QueryRequest = new QueryRequestPlan
            {
                TypeName = "ItemListRequest",
                DerivesFromListRequest = true,
                Properties =
                [
                    QueryProperty("limit", "Limit", QueryValueKind.Int32, isInherited: true, description: "Maximum item count."),
                    QueryProperty("order", "Order", QueryValueKind.ListOrder, isInherited: true),
                    QueryProperty("cursor", "Cursor", QueryValueKind.Text, isInherited: true, description: "Opaque item cursor."),
                ],
            },
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetItemListResponse",
                AdapterTypeName = "WidgetItemListResponseAdapter",
                PayloadName = "Items",
                PayloadType = ListOf(Named("ExampleItem")),
                Kind = EnvelopeKind.CursorList,
                SuccessStatusCode = 200,
                EnvelopeDtoTypeName = "WidgetItemListResponseEnvelope",
            },
            Pagination = new PaginationPlan
            {
                MethodName = "EnumerateItemsAsync",
                RequestTypeName = "ItemListRequest",
                PageTypeName = "WidgetItemListResponse",
                ItemTypeName = "ExampleItem",
                PayloadName = "Items",
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses =
                [
                    new ErrorStatusPlan
                    {
                        StatusCode = 404,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "MissingError",
                                TypeName = "MissingError",
                            }
                        ],
                    },
                ],
            },
            Summary = "List the widget items",
            Description = null,
        };

    /// <summary>A create operation with an optional all-optional request body.</summary>
    private static OperationPlan CreateWidgetCreateOperation() =>
        new()
        {
            OperationId = "widget.create",
            MethodName = "CreateWidgetAsync",
            HttpMethod = "post",
            RouteTemplate = "/api/widget",
            RouteContainerName = "Widgets",
            RouteMemberName = "CreateWidget",
            Parameters = [],
            QueryRequest = new QueryRequestPlan
            {
                TypeName = "WidgetCreateRequest",
                DerivesFromListRequest = false,
                RidesRequestBody = true,
                Properties = [QueryProperty("location", "Location", QueryValueKind.Location)],
            },
            RequestBody = new RequestBodyPlan
            {
                TypeName = "WidgetCreateRequest",
                ParameterName = "request",
                IsOptional = true,
            },
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetCreateResponse",
                AdapterTypeName = "WidgetCreateResponseAdapter",
                PayloadName = "Widget",
                PayloadType = Named("ExampleItem"),
                Kind = EnvelopeKind.Data,
                SuccessStatusCode = 200,
                EnvelopeDtoTypeName = "WidgetCreateResponseEnvelope",
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses =
                [
                    new ErrorStatusPlan
                    {
                        StatusCode = 400,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "BadRequestError",
                                TypeName = "BadRequestError",
                            }
                        ],
                    },
                ],
            },
            Summary = "Create one widget",
            Description = null,
        };

    private static OperationPlan CreateWidgetRemoveOperation() =>
        new()
        {
            OperationId = "widget.remove",
            MethodName = "RemoveWidgetAsync",
            HttpMethod = "delete",
            RouteTemplate = "/api/widget/{widgetID}",
            RouteContainerName = "Widgets",
            RouteMemberName = "RemoveWidget",
            Parameters =
            [
                new OperationParameterPlan
                {
                    WireName = "widgetID",
                    Name = "widgetId",
                    TypeName = "string",
                    IsHandleParameter = false,
                },
            ],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetRemoveResponse",
                AdapterTypeName = "WidgetRemoveResponseAdapter",
                PayloadName = null,
                PayloadType = null,
                Kind = EnvelopeKind.NoContent,
                SuccessStatusCode = 204,
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses =
                [
                    new ErrorStatusPlan
                    {
                        StatusCode = 400,
                        Tags =
                        [
                            new ErrorTagPlan
                            {
                                Tag = "BadRequestError",
                                TypeName = "BadRequestError",
                            }
                        ],
                    },
                ],
            },
            Summary = "Remove one widget",
            Description = null,
        };

    private static OperationPlan CreateWidgetSpotOperation() =>
        new()
        {
            OperationId = "widget.spot.get",
            MethodName = "GetWidgetSpotAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/widget-spot",
            RouteContainerName = "Widgets",
            RouteMemberName = "GetWidgetSpot",
            Parameters = [],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetSpotResponse",
                AdapterTypeName = "WidgetSpotResponseAdapter",
                PayloadName = "Spot",
                PayloadType = Named("ExampleItem"),
                Kind = EnvelopeKind.DataLocation,
                SuccessStatusCode = 200,
                EnvelopeDtoTypeName = "WidgetSpotResponseEnvelope",
                LocationTypeName = "ExamplePlace",
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses = [],
            },
            Summary = "Get one widget spot",
            Description = null,
        };

    private static OperationPlan CreateWidgetSpotListOperation() =>
        new()
        {
            OperationId = "widget.spot.list",
            MethodName = "ListWidgetSpotsAsync",
            HttpMethod = "get",
            RouteTemplate = "/api/widget-spot-list",
            RouteContainerName = "Widgets",
            RouteMemberName = "ListWidgetSpots",
            Parameters = [],
            Envelope = new EnvelopePlan
            {
                ResponseTypeName = "WidgetSpotListResponse",
                AdapterTypeName = "WidgetSpotListResponseAdapter",
                PayloadName = "Spots",
                PayloadType = ListOf(Named("ExampleItem")),
                Kind = EnvelopeKind.DataLocationList,
                SuccessStatusCode = 200,
                EnvelopeDtoTypeName = "WidgetSpotListResponseEnvelope",
                LocationTypeName = "ExamplePlace",
            },
            ErrorMap = new ErrorMapPlan
            {
                Statuses = [],
            },
            Summary = "List widget spots",
            Description = null,
        };

    private static ObjectModelPlan CreateExamplePlace() =>
        new()
        {
            Name = "ExamplePlace",
            Namespace = "OpenCode.Sdk.Models",
            Description = "Represents the resolved example place.",
            Properties =
            [
                Property("directory", "Directory", Named("string"), isRequired: true, "Gets the resolved directory."),
            ],
        };

    private static QueryPropertyPlan QueryProperty(string wireName, string propertyName, QueryValueKind kind,
        bool isInherited = false, string? description = null, bool isRequired = false, string? enumTypeName = null) =>
        new()
        {
            WireName = wireName,
            PropertyName = propertyName,
            Kind = kind,
            EnumTypeName = enumTypeName,
            Description = description,
            IsRequired = isRequired,
            IsInherited = isInherited,
        };

    private static ObjectModelPlan CreateWidgetCreateRequest() =>
        new()
        {
            Name = "WidgetCreateRequest",
            Namespace = "OpenCode.Sdk.Models",
            Description = "Shapes the widget-create request body.",
            Properties =
            [
                Property("title", "Title", Named("string", isNullable: true), isRequired: false, "Gets the widget title.",
                    emitsOptionalWrapper: true),
                Property("retain", "Retain", Named("bool", isNullable: true), isRequired: false,
                    "Gets whether the widget survives a restart.", emitsOptionalWrapper: true),
                Property("note", "Note", Named("string", isNullable: true), isRequired: false, "Gets the widget note."),
            ],
            RequestQueryProperties = [QueryProperty("location", "Location", QueryValueKind.Location)],
        };

    public static EmitPlan CreateModelSnapshot()
    {
        var plan = Create();
        return plan with
        {
            Models =
            [
                plan.Models.Single(static model => model.Name == "BadRequestError"),
                plan.Models.Single(static model => model.Name == "ExampleItem"),
                plan.Models.Single(static model => model.Name == "ExampleMode"),
                plan.Models.Single(static model => model.Name == "WidgetCreateRequest"),
                plan.Models.Single(static model => model.Name == "OpenSettings"),
            ],
            Unions = [plan.Unions.Single(static union => union.Name == "IOpenCodeError")],
        };
    }

    /// <summary>A model whose remaining members the document leaves open: named members plus the extension-data bag.</summary>
    private static ObjectModelPlan CreateOpenSettings() =>
        new()
        {
            Name = "OpenSettings",
            Namespace = "OpenCode.Sdk.Models",
            Description = "Represents settings whose remaining members the document leaves open.",
            Properties =
            [
                Property("timeout", "Timeout", Named("double", isNullable: true), isRequired: false, "Gets the timeout value."),
            ],
            EmitsExtensionData = true,
        };

    public static IReadOnlyList<UnionPlan> CreateUnionSnapshot() => [CreateExampleEvent(), CreateExamplePhase()];

    /// <summary>A nested union whose outer tag is fixed; both converters must enforce it.</summary>
    private static UnionPlan CreateExamplePhase() =>
        new()
        {
            Name = "IExamplePhase",
            ConceptName = "ExamplePhase",
            Namespace = "OpenCode.Sdk.Models",
            UnknownTypeName = "UnknownExamplePhase",
            MarkerWireName = "status",
            MarkerName = "Status",
            MarkerKind = LiteralKind.String,
            BaseTypeName = "IExampleEvent",
            FixedMarker = new UnionFixedMarkerPlan
            {
                WireName = "type",
                Name = "Type",
                Kind = LiteralKind.String,
                Value = "phase",
            },
            Variants =
            [
                new UnionVariantPlan
                {
                    TypeName = "PhaseStartedEvent",
                    Tag = "started",
                    MarkerWireName = "status",
                }
            ],
            Description = "Represents an example nested phase union.",
        };

    public static RegistryPlan CreateRegistry() => Create().Registry;

    private static ObjectModelPlan CreateExampleItem() =>
        new()
        {
            Name = "ExampleItem",
            Namespace = "OpenCode.Sdk.Models",
            Description = "Represents one emitted item.",
            Properties =
            [
                Property("id", "ID", Named("string"), isRequired: true, "Gets the item identifier."),
                Property("note", "Note", Named("string", isNullable: true), isRequired: false, description: null),
                Property("count", "Count", Named("double", isNullable: true), isRequired: false, "Gets the item count."),
                Property("ratio", "Ratio", SpecialNumber(isNullable: true), isRequired: false, "Gets the item ratio."),
                Property("peak", "Peak", SpecialNumber(), isRequired: true, "Gets the item peak."),
                Property("requiredNullable", "RequiredNullable", Named("string", isNullable: true), isRequired: true,
                    "Gets a required nullable value."),
                Property("flushedAt", "FlushedAt", Named("double", isNullable: true), isRequired: false,
                    "Gets the flush timestamp, or null when never flushed."),
                Property("tags", "Tags", ListOf(Named("string"), isNullable: true), isRequired: false, "Gets the item tags."),
                Property("links", "Links", DictionaryOf(Named("Uri"), isNullable: true), isRequired: false, "Gets links by relation."),
                Property("requiredTags", "RequiredTags", ListOf(Named("string")), isRequired: true, "Gets required tags."),
            ],
        };

    private static EnumModelPlan CreateExampleMode() =>
        new()
        {
            Name = "ExampleMode",
            Namespace = "OpenCode.Sdk.Models",
            Description = null,
            Values =
            [
                new EnumValuePlan
                {
                    Name = "FastMode",
                    WireValue = "fast-mode",
                },
                new EnumValuePlan
                {
                    Name = "Safe",
                    WireValue = "safe",
                },
            ],
        };

    private static ObjectModelPlan CreateVariant(string name, string tag, string wireName, string propertyName,
        TypeReferencePlan propertyType) =>
        new()
        {
            Name = name,
            Namespace = "OpenCode.Sdk.Models",
            Description = null,
            ImplementedUnionNames = ["IExampleEvent"],
            Properties =
            [
                LiteralProperty("type", "Type", tag),
                Property(wireName, propertyName, propertyType, isRequired: true, description: null),
            ],
        };

    /// <summary>
    /// The example event union's prefix-tagged arm: it carries the union's marker as a required
    /// string the prefix claims rather than as a literal, so it is never one of the union's
    /// literal variants.
    /// </summary>
    private static ObjectModelPlan CreateRpcEvent() =>
        new()
        {
            Name = "RpcEvent",
            Namespace = "OpenCode.Sdk.Models",
            Description = null,
            ImplementedUnionNames = ["IExampleEvent"],
            Properties =
            [
                Property("type", "Type", Named("string"), isRequired: true, description: null),
                Property("item", "Item", Named("ExampleItem"), isRequired: true, description: null),
            ],
        };

    private static ObjectModelPlan CreateError() =>
        new()
        {
            Name = "BadRequestError",
            Namespace = "OpenCode.Sdk.Models",
            Description = "Represents a rejected request.",
            ImplementedUnionNames = ["IOpenCodeError"],
            Properties =
            [
                LiteralProperty("_tag", "Tag", "BadRequestError"),
                Property("message", "Message", Named("string"), isRequired: true, "Gets the error message."),
            ],
        };

    private static UnionPlan CreateExampleEvent() =>
        new()
        {
            Name = "IExampleEvent",
            ConceptName = "ExampleEvent",
            Namespace = "OpenCode.Sdk.Models",
            UnknownTypeName = "UnknownExampleEvent",
            MarkerWireName = "type",
            MarkerName = "Type",
            MarkerKind = LiteralKind.String,
            Variants =
            [
                new UnionVariantPlan
                {
                    TypeName = "CreatedEvent",
                    Tag = "created",
                    MarkerWireName = "type",
                },
                new UnionVariantPlan
                {
                    TypeName = "DeletedEvent",
                    Tag = "deleted",
                    MarkerWireName = "type",
                },
            ],
            PrefixVariant = new UnionPrefixVariantPlan
            {
                TypeName = "RpcEvent",
                Prefix = "rpc.",
                MarkerWireName = "type",
            },
            Description = "Represents an example event.",
        };

    private static UnionPlan CreateOpenCodeError() =>
        new()
        {
            Name = "IOpenCodeError",
            ConceptName = "IOpenCodeError",
            Namespace = "OpenCode.Sdk.Models",
            UnknownTypeName = "UnknownOpenCodeError",
            MarkerWireName = "_tag",
            MarkerName = "Tag",
            MarkerKind = LiteralKind.String,
            Variants =
            [
                new UnionVariantPlan
                {
                    TypeName = "BadRequestError",
                    Tag = "BadRequestError",
                    MarkerWireName = "_tag",
                }
            ],
            Description = "Represents an opencode API error.",
        };

    private static ModelPropertyPlan Property(string wireName, string name, TypeReferencePlan type, bool isRequired,
        string? description, bool emitsOptionalWrapper = false) =>
        new()
        {
            WireName = wireName,
            Name = name,
            Type = type,
            IsRequired = isRequired,
            IsLiteral = false,
            Description = description,
            EmitsOptionalWrapper = emitsOptionalWrapper,
        };

    private static ModelPropertyPlan LiteralProperty(string wireName, string name, string value) =>
        new()
        {
            WireName = wireName,
            Name = name,
            Type = Named("string"),
            IsRequired = true,
            IsLiteral = true,
            LiteralKind = LiteralKind.String,
            LiteralValue = value,
            Description = null,
            EmitsOptionalWrapper = false,
        };

    private static NamedTypeReferencePlan Named(string name, bool isNullable = false) =>
        new()
        {
            Name = name,
            IsNullable = isNullable,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    private static SpecialNumberTypeReferencePlan SpecialNumber(bool isNullable = false) =>
        new()
        {
            IsNullable = isNullable,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    private static ListTypeReferencePlan ListOf(TypeReferencePlan elementType, bool isNullable = false) =>
        new()
        {
            ElementType = elementType,
            IsNullable = isNullable,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    private static DictionaryTypeReferencePlan DictionaryOf(TypeReferencePlan valueType, bool isNullable = false) =>
        new()
        {
            ValueType = valueType,
            IsNullable = isNullable,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    /// <summary>
    /// A marked union whose arms share an id, a timestamp, and a per-arm promoted durable
    /// envelope, nested under a second union that shares one more member. The plan runs through
    /// the production hoist binder so the emitters are exercised against the shape the binder
    /// actually produces, with the curated carrier name the repository uses for the envelope.
    /// </summary>
    public static EmitPlan CreateHoistPlan()
    {
        var models = new ModelPlan[]
        {
            HoistVariant("CreatedEvent", "created", "IExampleEvent"),
            HoistVariant("DeletedEvent", "deleted", "IExampleEvent"),
            HoistPhase("PhaseStartedEvent", "started"),
            HoistPhase("PhaseEndedEvent", "ended"),
            HoistEnvelope("CreatedEventDurable", "1"),
            HoistEnvelope("DeletedEventDurable", "2"),
            HoistEnvelope("PhaseStartedEventDurable", "1"),
            HoistEnvelope("PhaseEndedEventDurable", "2"),
        };
        var unions = new[] { CreateHoistEvent(), CreateHoistPhase(), };
        var errors = new BindingErrorCollector();
        var hoisted = UnionMemberHoistBinder.Bind(unions, models, HoistCuration(), errors);
        errors.ThrowIfAny();
        return new EmitPlan
        {
            ImplicitAliases = StabilizeDuplicateCollapse.Empty,
            SelectedOperationIds = [],
            Models = hoisted.Models,
            Unions = hoisted.Unions,
            HoistedInterfaces = hoisted.Interfaces,
            Clients = [],
            Registry = new RegistryPlan
            {
                TypeNames =
                [
                    .. hoisted
                        .Models.Select(static model => model.Name)
                        .Concat(hoisted.Unions.SelectMany(static union => new[] { union.Name, union.UnknownTypeName, }))
                        .Order(StringComparer.Ordinal),
                ],
            },
            PendingOperations = [],
            DeclinedOperations = [],
            TransportOwnedOperationIds = [],
        }.WithSingleSlice();
    }

    private static GenerationCuration HoistCuration() =>
        Curation(
            Groups(),
            hoistedMemberNames:
            [
                HoistedMemberName("IExampleEvent", "durable", "IDurableEnvelope",
                    "The domain calls this shape the durable envelope."),
            ]);

    private static ObjectModelPlan HoistVariant(string name, string tag, string unionName) =>
        new()
        {
            Name = name,
            Namespace = "OpenCode.Sdk.Models",
            Description = null,
            ImplementedUnionNames = [unionName],
            Properties =
            [
                Property("id", "Id", Named("string"), isRequired: true, "Gets the event identifier."),
                Property("created", "Created", Named("double"), isRequired: true, "Gets the creation timestamp."),
                LiteralProperty("type", "Type", tag),
                Property("durable", "Durable", Named($"{name}Durable"), isRequired: true, "Gets the durable envelope."),
            ],
        };

    private static ObjectModelPlan HoistPhase(string name, string tag)
    {
        var variant = HoistVariant(name, "phase", "IExamplePhase");
        return variant with
        {
            Properties =
            [
                .. variant.Properties,
                LiteralProperty("status", "Status", tag),
                Property("phase", "Phase", Named("string"), isRequired: true, "Gets the phase name."),
            ],
        };
    }

    private static ObjectModelPlan HoistEnvelope(string name, string version) =>
        new()
        {
            Name = name,
            Namespace = "OpenCode.Sdk.Models",
            Description = null,
            Properties =
            [
                Property("seq", "Seq", Named("long"), isRequired: true, "Gets the aggregate sequence."),
                new ModelPropertyPlan
                {
                    WireName = "version",
                    Name = "Version",
                    Type = Named("double"),
                    IsRequired = true,
                    IsLiteral = true,
                    LiteralKind = LiteralKind.Number,
                    LiteralValue = version,
                    Description = "Gets the schema version that committed the event.",
                    EmitsOptionalWrapper = false,
                },
            ],
        };

    private static UnionPlan CreateHoistEvent() =>
        new()
        {
            Name = "IExampleEvent",
            ConceptName = "ExampleEvent",
            Namespace = "OpenCode.Sdk.Models",
            UnknownTypeName = "UnknownExampleEvent",
            MarkerWireName = "type",
            MarkerName = "Type",
            MarkerKind = LiteralKind.String,
            Variants =
            [
                new UnionVariantPlan { TypeName = "CreatedEvent", Tag = "created", MarkerWireName = "type" },
                new UnionVariantPlan { TypeName = "DeletedEvent", Tag = "deleted", MarkerWireName = "type" },
                new UnionVariantPlan { TypeName = "IExamplePhase", Tag = "phase", MarkerWireName = "type", IsNestedUnion = true },
            ],
            Description = "Represents an example event.",
        };

    private static UnionPlan CreateHoistPhase() =>
        new()
        {
            Name = "IExamplePhase",
            ConceptName = "ExamplePhase",
            Namespace = "OpenCode.Sdk.Models",
            UnknownTypeName = "UnknownExamplePhase",
            MarkerWireName = "status",
            MarkerName = "Status",
            MarkerKind = LiteralKind.String,
            BaseTypeName = "IExampleEvent",
            FixedMarker = new UnionFixedMarkerPlan
            {
                WireName = "type",
                Name = "Type",
                Kind = LiteralKind.String,
                Value = "phase",
            },
            Variants =
            [
                new UnionVariantPlan { TypeName = "PhaseStartedEvent", Tag = "started", MarkerWireName = "status" },
                new UnionVariantPlan { TypeName = "PhaseEndedEvent", Tag = "ended", MarkerWireName = "status" },
            ],
            Description = "Represents an example nested phase union.",
        };
}
