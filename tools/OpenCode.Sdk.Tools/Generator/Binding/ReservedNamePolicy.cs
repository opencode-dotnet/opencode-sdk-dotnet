using System.Collections.Frozen;

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

    /// <summary>
    /// Hand-written public spine types in the generated namespaces; a generated twin would not
    /// compile. Both PTY families' public doors are here because ADR-0021 gives their public
    /// surface to hand-written code while the generator keeps its raw twins — including each
    /// family's WebSocket session, its options, and its frame hierarchy, which the pinned
    /// document cannot describe at all because the connect operations are transport-owned.
    /// </summary>
    public static readonly FrozenSet<string> SpineTypeNames = FrozenSet.ToFrozenSet(
        [
            "CursorSequence",
            "ErrorBehavior",
            EffectStreamTypeNamePolicy.CauseMarkerInterface,
            "ListCursor",
            "ListOrder",
            "ListRequest",
            "LocationSelector",
            "OpenCodeApiException",
            "OpenCodeClientOptions",
            "OpenCodeException",
            "OpenCodeRequestOptions",
            "OpenCodeResponse",
            "OpenCodeServer",
            "OpenCodeServerDiscoverOptions",
            "OpenCodeServerEnsureOptions",
            "OpenCodeServerEnsureReason",
            "OpenCodeServerException",
            "OpenCodeServerExit",
            "OpenCodeServerExitKind",
            "OpenCodeServerOptions",
            "OpenCodeServerOutput",
            "OpenCodeServerOutputSnapshot",
            "OpenCodeServerStopOptions",
            "OpenCodeServerVersionPolicy",
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
            "PtyClient",
            "PtyConnectOptions",
            "PtyCursorFrame",
            "PtyFrame",
            "PtyOutputFrame",
            "PtysClient",
            "PtySession",
            "QueryBoolean",
            "SessionParentFilter",
        ],
        StringComparer.Ordinal);

    /// <summary>Parameter names the emitted method and route-builder signatures append themselves.</summary>
    public static readonly FrozenSet<string> ParameterNames = FrozenSet.ToFrozenSet(
        [
            CancellationTokenParameter,
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
            "BatchCommandLine",
            "BoundedLineBuffer",
            "CallerStreamContent",
            "CancellationTokenSourceExtensions",
            "ClientTerminalWebSocket",
            "ConnectionSnapshot",
            "CursorPaginator",
            "DeclaredHeader",
            "EmptyOpenMembers",
            "EncodedResponseBody",
            "EndpointPolicy",
            "ExecutableResolver",
            "ExecutableSearchEnvironment",
            "FailureClassification",
            "FailurePhase",
            "FrameDispatch",
            "ICursorPageAdapter",
            "IEventStreamFramer",
            "IStreamAdapter",
            "ITerminalClosePolicy",
            "ITerminalFrameDecoder",
            "ITerminalUpgradeFailurePolicy",
            "ITerminalWebSocket",
            "LocationMerge",
            "MockSeam",
            "OpenCodeErrorReader",
            "OpenCodeHttpMethod",
            "PersistentPtyClosePolicy",
            "PersistentPtyConnectUriBuilder",
            "PersistentPtyFrameDecoder",
            "PersistentPtyInputFrame",
            "PersistentPtyUpgradeFailurePolicy",
            "Pipeline",
            "PipelineMessage",
            "PipelinePolicy",
            "ProcessArgumentComposer",
            "ProgressWindow",
            "PtyCloseFailurePolicy",
            "PtyClosePolicy",
            "PtyConnectUriBuilder",
            "PtyFrameDecoder",
            "PtyFrameReader",
            "PtyMessageAssembler",
            "PtyReceiveResult",
            "PtyTicketHeader",
            "PtyUpgradeFailurePolicy",
            "QueryStringBuilder",
            "RecordPrinter",
            "RequestDecorationPolicy",
            "ResolvedExecutable",
            "ResponseAdapter",
            "ResponseBody",
            "ResponseBufferingPolicy",
            "ResponseEncodingPolicy",
            "ResponseMaterializer",
            "RouteValuePolicy",
            "ServerReadyLine",
            "ServerSentEvent",
            "ServerSentEventFramer",
            "ServerSentEventReader",
            "StatusVerdict",
            "StatusVerdictFailures",
            "StrictJsonStringEnumConverter",
            "StructuralUnionClaim",
            "StructuralUnionPrinter",
            "TerminalCancellationSource",
            "TerminalConnectionOutcome",
            "TerminalDeadlineFactory",
            "TerminalReceivePump",
            "TerminalSendActions",
            "TerminalSendDeadline",
            "TerminalSendQueue",
            "TerminalSendTimeout",
            "TerminalSocketBounds",
            "TerminalSocketCore",
            "TerminalSubmission",
            "TransportPolicy",
            "UnionDiscriminatorReader",
            "UnionPayloadGuard",
            "UserAgentPolicy",
            "WebSocketSchemePolicy",
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
