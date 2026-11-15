using System.Collections.Frozen;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Single owner of every name the emitted surface claims for itself. The binder refuses wire
/// names landing on these sets, and the emitters render the parameter names from the same
/// constants, so the two sides cannot drift apart silently; the reflection tests assert the
/// type and member sets against the hand-written <c>OpenCode.Sdk</c> surface.
/// </summary>
internal static class ReservedNamePolicy
{
    /// <summary>Parameter name every emitted operation method appends for cancellation.</summary>
    public const string CancellationTokenParameter = "cancellationToken";

    /// <summary>Local and argument name an emitted operation method uses for its declared headers.</summary>
    public const string DeclaredHeadersParameter = "declaredHeaders";

    /// <summary>Parameter name emitted constructors take for the shared pipeline spine.</summary>
    public const string PipelineParameter = "pipeline";

    /// <summary>Parameter name emitted methods and route builders use for the operation request.</summary>
    public const string RequestParameter = "request";

    /// <summary>Parameter name emitted methods append for per-call options.</summary>
    public const string RequestOptionsParameter = "requestOptions";

    public const string ContentParameter = "content";
    public const string PipelineProperty = "Pipeline";
    public const string PipelineOrNullProperty = "PipelineOrNull";
    public const string PipelineField = "_pipeline";
    public const string DisposeMethod = "Dispose";
    public const string EmptyBodyPrefix = "Empty";
    public const string RoutesType = "OpenCodeRoutes";
    public const string JsonContextType = "OpenCodeJsonContext";
    public const string QueryEnumConverter = "ToWireValue";

    /// <summary>Identifies the declaration that a refused route name collides with.</summary>
    public static string RouteNameProblem(OperationPlan operation) => operation.RouteContainerName switch
    {
        var container when string.Equals(container, operation.RouteMemberName, StringComparison.Ordinal) =>
            $"route container '{container}' collides with its own route member '{operation.RouteMemberName}'",
        RoutesType or QueryEnumConverter =>
            $"route container '{operation.RouteContainerName}' collides with a name the routes root declares",
        _ => $"route member '{operation.RouteMemberName}' collides with a name the routes root declares",
    };

    public static string SubClientFieldName(ClientReferencePlan reference) =>
        $"_{CSharpNamePolicy.ToCamelCase(reference.PropertyName)}";

    public static string HandleFieldName(OperationParameterPlan parameter) => $"_{parameter.Name}";

    /// <summary>Names the client's cached empty instance for an optional request-body type.</summary>
    public static string EmptyBodyFieldName(string typeName) => $"{EmptyBodyPrefix}{typeName}";

    /// <summary>Distinct optional request-body types, in operation order.</summary>
    public static IEnumerable<string> OptionalBodyTypeNames(ClientPlan client)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in client.Operations)
        {
            if (operation.RequestBody is { IsOptional: true } body && seen.Add(body.TypeName))
            {
                yield return body.TypeName;
            }
        }
    }

    /// <summary>
    /// Hand-written public spine types in the generated namespaces; a generated twin would not
    /// compile. The PTY families' and the Events family's public doors are here because ADR-0021
    /// gives their public surface to hand-written code while the generator keeps its raw twins —
    /// including the PTY families' WebSocket sessions, options, and frame hierarchies, which the pinned
    /// document cannot describe at all because the connect operations are transport-owned.
    /// </summary>
    public static readonly FrozenSet<string> SpineTypeNames = FrozenSet.ToFrozenSet(
        [
            "ClientMcpContent",
            "ClientMcpOptions",
            "ClientMcpSchema",
            "ClientMcpTool",
            "ClientMcpToolContext",
            "ClientMcpToolResult",
            "CursorSequence",
            "ErrorBehavior",
            "EventConnectionState",
            "EventConnectionStatus",
            "EventsClient",
            "EventsClientRpcExtensions",
            EffectStreamTypeNamePolicy.CauseMarkerInterface,
            SessionScopedEventContract.InterfaceName,
            "LanguageModelsClientExtensions",
            "ListCursor",
            "ListOrder",
            "ListRequest",
            "LocationSelector",
            "MediaReference",
            "OpenCodeApiException",
            "OpenCodeClientOptions",
            "OpenCodeException",
            "OpenCodeEventSubscriberOverflowException",
            "OpenCodeRequestOptions",
            "OpenCodeResponse",
            "OpenCodeRpcException",
            "OpenCodeServer",
            "OpenCodeServerConnection",
            "OpenCodeServerConnectionMode",
            "OpenCodeServerDiscoverOptions",
            "OpenCodeServerEnsureOptions",
            "OpenCodeServerEnsureReason",
            "OpenCodeServerEnsureStartContext",
            "OpenCodeServerException",
            "OpenCodeServerExit",
            "OpenCodeServerExitKind",
            "OpenCodeServerIncumbent",
            "OpenCodeServerOptions",
            "OpenCodeServerOutput",
            "OpenCodeServerOutputSnapshot",
            "OpenCodeServerResolveOptions",
            "OpenCodeServerServiceOperations",
            "OpenCodeServerState",
            "OpenCodeServerStopOptions",
            "OpenCodeServerStopPtyMode",
            "OpenCodeServerVersionMismatchContext",
            "OpenCodeServerVersionPolicy",
            "OpenCodeServiceConfig",
            "OpenCodeServiceConfigOptions",
            "OpenCodeServiceConfiguration",
            "OpenCodeServiceSetting",
            "OpenCodeStreamFailureException",
            "OpenCodeTransportException",
            "Optional",
            "PersistentPtyAttachedFrame",
            "PersistentPtyAttachment",
            "PersistentPtyClient",
            "PersistentPtyConnectOptions",
            "PersistentPtyControllerChangedFrame",
            "PersistentPtyExitedFrame",
            "PersistentPtyForegroundProcessChangedFrame",
            "PersistentPtyFrame",
            "PersistentPtyOutputFrame",
            "PersistentPtyReplayBounds",
            "PersistentPtyReplayCompleteFrame",
            "PersistentPtyResizedFrame",
            "PersistentPtyRole",
            "PersistentPtysClient",
            "PersistentPtySession",
            "PersistentPtyTitleChangedFrame",
            "PersistentPtyUnknownFrame",
            "PluginsClientExtensions",
            "PtyClient",
            "PtyConnectOptions",
            "PtyCursorFrame",
            "PtyFrame",
            "PtyOutputFrame",
            "PtysClient",
            "PtySession",
            "RpcClientExtensions",
            "RpcDefinition",
            "RpcEventDefinition",
            "RpcEventPayload",
            "RpcMethod",
            "SessionParentFilter",
        ],
        StringComparer.Ordinal);

    /// <summary>Parameter names the emitted method and route-builder signatures append themselves.</summary>
    public static readonly FrozenSet<string> ParameterNames = FrozenSet.ToFrozenSet(
        [
            CancellationTokenParameter,
            ContentParameter,
            DeclaredHeadersParameter,
            PipelineParameter,
            RequestParameter,
            RequestOptionsParameter,
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Hand-written types in the internal namespace. Every generated internal type declares that
    /// namespace and every generated file imports it, so a generated type of the same name would
    /// either not compile (CS0101) or make the name ambiguous (CS0104); emission refuses it.
    /// </summary>
    public static readonly FrozenSet<string> InternalTypeNames = FrozenSet.ToFrozenSet(
        [
            "ActivityStream",
            "AfterGate",
            "BatchCommandLine",
            "BoundedLineBuffer",
            "BoundedWait",
            "CallerStreamContent",
            "CancellationTokenSourceExtensions",
            "ChildEnvironmentComposer",
            "ChildExitStatus",
            "ChildExitStatusReader",
            "ChildPipe",
            "ChildStreamEnd",
            "ChildStreamRoute",
            "ChildStreamSet",
            "ClientIdentityComposer",
            "ClientLifetime",
            "ClientMcpConnectUriBuilder",
            "ClientMcpConnector",
            "ClientMcpUpgradeFailurePolicy",
            "ClientTerminalWebSocket",
            "ConnectionResolver",
            "ConnectionSeams",
            "ConnectionSnapshot",
            "CorsOriginPolicy",
            "CursorPaginator",
            "DeclaredHeader",
            "DefaultModelWait",
            "Delivery",
            "EmptyOpenMembers",
            "EncodedResponseBody",
            "EndpointPolicy",
            "EnsureRequest",
            "ErrorBehaviorPolicy",
            "EventAttemptSource",
            "EventFeedBounds",
            "EventPendingMove",
            "EventSelection",
            "EventSource",
            "EventSourceRunner",
            "EventStateWatcher",
            "EventSubscription",
            "ExecutableResolver",
            "ExecutableSearchEnvironment",
            "ExitWaitKernel",
            "ExperimentalDiagnostics",
            "ExplicitProbeFailureClassifier",
            "ExplicitServerProbe",
            "FailureClassification",
            "FailurePhase",
            "FileRenameKernel",
            "FrameDispatch",
            "IChildExitStatus",
            "ICursorPageAdapter",
            "IEventStreamFramer",
            "IEventAttemptSource",
            "IEventFeedGateProbe",
            "IExitWaitKernel",
            "IFileRenameKernel",
            "IJobObjects",
            "ILoopbackSocketConnector",
            "IPosixSpawn",
            "IProcessGroupSignal",
            "IProcessJob",
            "IServerDoors",
            "IServerLadder",
            "IServiceCliRunner",
            "IServiceContender",
            "IServiceContenderSpawner",
            "IServiceEnvironment",
            "IServiceFileSystem",
            "IServiceInfoProbe",
            "IServiceProbeTransport",
            "IServiceProcessControl",
            "IServicePtyHandoff",
            "IServicePtyShutdown",
            "IStreamAdapter",
            "ITerminalClosePolicy",
            "ITerminalFrameDecoder",
            "ITerminalUpgradeFailurePolicy",
            "ITerminalWebSocket",
            "IWindowsChildExit",
            "IWindowsExitWatch",
            "IWindowsSpawn",
            "IWindowsTreeKill",
            "IWorkingDirectoryAction",
            "InheritedHandleList",
            "JobAssignment",
            "JobObjects",
            "JsonBodyWriter",
            "JsonElementDictionaryJsonConverter",
            "KillOnCloseJob",
            "LauncherSeams",
            "LocationMerge",
            "LoopbackConnect",
            "LoopbackConnectAttempt",
            "LoopbackSocketConnector",
            "LoopbackTransport",
            "ManagedServiceSnapshot",
            "MockSeam",
            "NetworkTimeouts",
            "NoOpEventFeedGateProbe",
            "NulText",
            "OpenCodeErrorReader",
            "OpenCodeHttpMethod",
            "OperationRequest",
            "OperationTraits",
            "OwnedServerChild",
            "OwnedServerProcess",
            "PersistentPtyClosePolicy",
            "PersistentPtyConnectUriBuilder",
            "PersistentPtyFrameDecoder",
            "PersistentPtyInputFrame",
            "PersistentPtyUpgradeFailurePolicy",
            "PinnedRelease",
            "PipeLineReader",
            "PipeOutputPump",
            "Pipeline",
            "PipelineExchange",
            "PipelineMessage",
            "PipelinePolicy",
            "PluginWait",
            "PooledJsonContent",
            "PosixChildExitWatch",
            "PosixInterop",
            "PosixPlatform",
            "PosixServerChild",
            "PosixServerLadder",
            "PosixSignalNames",
            "PosixSpawn",
            "PosixSpawnRequest",
            "PosixSpawnedChild",
            "ProcessArgumentComposer",
            "ProcessGroupSignal",
            "ProcessIdentity",
            "ProcessSignal",
            "ProgressWindow",
            "PtyCloseFailurePolicy",
            "PtyClosePolicy",
            "PtyConnectUriBuilder",
            "PtyFrameDecoder",
            "PtyFrameReader",
            "PtyHandoffSidecar",
            "PtyMessageAssembler",
            "PtyReceiveResult",
            "PtyTicketHeader",
            "PtyUpgradeFailurePolicy",
            "QueryStringBuilder",
            "RandomCredential",
            "ReadinessOutcome",
            "RecordPrinter",
            "RequestDecorationPolicy",
            "ResolveRequest",
            "ResolvedExecutable",
            "ResponseAdapter",
            "ResponseBufferingPolicy",
            "ResponseEncodingPolicy",
            "ResponseMaterializer",
            "RouteValuePolicy",
            "RpcEventReader",
            "RpcMethodCaller",
            "SafeJobHandle",
            "SafeThreadHandle",
            "ServerChild",
            "ServerChildStart",
            "ServerDoors",
            "ServerReadyLine",
            "ServerSentEvent",
            "ServerSentEventFramer",
            "ServerSentEventReader",
            "ServiceCliRunner",
            "ServiceConfigDocument",
            "ServiceConfigEditor",
            "ServiceConfigEnvironment",
            "ServiceConfigEnvironmentReader",
            "ServiceConfigJsonWriter",
            "ServiceConfigReader",
            "ServiceConfigReadResult",
            "ServiceConfigStore",
            "ServiceContender",
            "ServiceContenderEnvironmentComposer",
            "ServiceContenderSpawner",
            "ServiceDisabledCheck",
            "ServiceDiscovery",
            "ServiceElection",
            "ServiceElectionSeams",
            "ServiceEnsurer",
            "ServiceEnvironment",
            "ServiceFileSystem",
            "ServiceHandoffTicketAdapter",
            "ServiceHandoffTicketResponse",
            "ServiceIncumbent",
            "ServiceInfoProbe",
            "ServiceLegacyFilename",
            "ServiceMigration",
            "ServicePathResolver",
            "ServicePaths",
            "ServiceProbeResponseClassifier",
            "ServiceProbeResult",
            "ServiceProcessControl",
            "ServicePtyHandoff",
            "ServicePtyHandoffStep",
            "ServicePtyShutdown",
            "ServiceRegistration",
            "ServiceRegistrationIdentity",
            "ServiceRegistrationLocator",
            "ServiceRegistrationReader",
            "ServiceRemoteAccess",
            "ServiceRemoteRequest",
            "ServiceSelection",
            "ServiceState",
            "ServiceStopper",
            "ServiceTerminator",
            "ServiceTiming",
            "ServiceVersionPolicy",
            "SignalDelivery",
            "SharedEventFeed",
            "SlopwatchSuppressAttribute",
            "StatusVerdict",
            "StatusVerdictFailures",
            "StopWork",
            "StreamAttempt",
            "StreamPlane",
            "StreamReconnect",
            "StrictJson",
            "StructuralUnionClaim",
            "StructuralUnionPrinter",
            "SubscriptionEnd",
            "TaskkillTreeKill",
            "TerminalConnectionOutcome",
            "TerminalReceivePump",
            "TerminalSendActions",
            "TerminalSendDeadline",
            "TerminalSendQueue",
            "TerminalSendTimeout",
            "TerminalSocketBounds",
            "TerminalSocketCore",
            "TerminalSubmission",
            "TimedCancellation",
            "TimerBounds",
            "TransportPolicy",
            "TreeKillOutcome",
            "UnionArm",
            "UnionDiscriminatorReader",
            "UnionMemberReader",
            "UnionPayloadGuard",
            "UnionTagTable",
            "UnionVariantRead",
            "UserAgentPolicy",
            "VersionMismatchNotifier",
            "WebSocketFailureContext",
            "WebSocketSchemePolicy",
            "WindowsChildExit",
            "WindowsCommand",
            "WindowsCommandLine",
            "WindowsEnvironmentBlock",
            "WindowsExitWatch",
            "WindowsInterop",
            "WindowsLadderBounds",
            "WindowsPlatform",
            "WindowsServerChild",
            "WindowsServerLadder",
            "WindowsSpawn",
            "WindowsSpawnRequest",
            "WindowsSpawnedChild",
            "WindowsStandardStreams",
            "WorkingDirectoryAction",
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Member names every generated response envelope inherits from the response spine or
    /// from <see cref="object"/>/record synthesis; a payload landing on one of them needs a
    /// curated override.
    /// </summary>
    public static readonly FrozenSet<string> PayloadNames = FrozenSet.ToFrozenSet(
        [
            "Cursor",
            "Deconstruct",
            "EqualityContract",
            "Equals",
            "Error",
            "GetHashCode",
            "GetType",
            "IsError",
            "Location",
            "MemberwiseClone",
            "PrintMembers",
            "RawBody",
            "ReferenceEquals",
            "Status",
            "ToString",
        ],
        StringComparer.Ordinal);
}
