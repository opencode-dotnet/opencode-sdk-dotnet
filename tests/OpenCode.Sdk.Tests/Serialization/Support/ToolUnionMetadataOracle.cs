using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Serialization.Support;

/// <summary>Keeps converted unions and their nested records on the metadata-only path.</summary>
internal sealed class ToolUnionMetadataOracle<TUnion> : MetadataUnionJsonConverter<TUnion>
{
    private static readonly bool State = typeof(TUnion) == typeof(ISessionMessageToolState);
    private static readonly bool Event = typeof(TUnion) == typeof(IEvent);
    protected override string Concept => typeof(TUnion).Name switch
    {
        nameof(IEvent) => "Event",
        nameof(ISessionMessageToolState) => "SessionMessageToolState",
        nameof(ISessionMessageInfo) => "SessionMessageInfo",
        nameof(ISessionMessageAssistantContent) => "SessionMessageAssistantContent",
        _ => "ToolContent",
    };
    protected override string Marker => State ? "status" : "type";
    protected override UnionTagTable<Type?> Types => Table;
    protected override string? Prefix => PrefixValue;
    protected override Type? PrefixType => PrefixTypeValue;
    public static string? PrefixValue => Event ? "rpc." : null;
    public static Type? PrefixTypeValue => Event ? typeof(EventRpc) : null;
    public static IReadOnlyDictionary<string, Type?> Map { get; } = CreateMap();
    private static Dictionary<string, Type?> CreateMap()
    {
        if (Event)
        {
            return EventTypes().Concat(SessionEventTypes()).ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        }
        if (typeof(TUnion) == typeof(ISessionMessageInfo))
        {
            return new Dictionary<string, Type?>(StringComparer.Ordinal)
            {
                ["agent-switched"] = typeof(SessionMessageAgentSelected),
                ["assistant"] = typeof(SessionMessageAssistant),
                ["compaction"] = typeof(ISessionMessageCompaction),
                ["idle"] = typeof(SessionMessageIdle),
                ["location-switched"] = typeof(SessionMessageLocationSwitched),
                ["model-switched"] = typeof(SessionMessageModelSelected),
                ["shell"] = typeof(SessionMessageShell),
                ["skill"] = typeof(SessionMessageSkill),
                ["synthetic"] = typeof(SessionMessageSynthetic),
                ["system"] = typeof(SessionMessageSystem),
                ["user"] = typeof(SessionMessageUser),
            };
        }
        if (typeof(TUnion) == typeof(ISessionMessageAssistantContent))
        {
            return new Dictionary<string, Type?>(StringComparer.Ordinal)
            {
                ["reasoning"] = typeof(SessionMessageAssistantReasoning),
                ["text"] = typeof(SessionMessageAssistantText),
                ["tool"] = typeof(SessionMessageAssistantTool),
            };
        }
        return State
        ? new Dictionary<string, Type?>(StringComparer.Ordinal)
        {
            ["completed"] = typeof(SessionMessageToolStateCompleted),
            ["error"] = typeof(SessionMessageToolStateError),
            ["running"] = typeof(SessionMessageToolStateRunning),
            ["streaming"] = typeof(SessionMessageToolStateStreaming),
        }
        : new Dictionary<string, Type?>(StringComparer.Ordinal)
        {
            ["file"] = typeof(ToolFileContent),
            ["text"] = typeof(ToolTextContent),
        };
    }
    private static readonly UnionTagTable<Type?> Table = Map.ToUnionTagTable();

    protected override TUnion Unknown(string marker, JsonElement payload) =>
        (TUnion)(typeof(TUnion).Name switch
        {
            nameof(IEvent) => (object)new UnknownEvent(marker, payload),
            nameof(ISessionMessageToolState) => new UnknownSessionMessageToolState(marker, payload),
            nameof(ISessionMessageInfo) => new UnknownSessionMessageInfo(marker, payload),
            nameof(ISessionMessageAssistantContent) => new UnknownSessionMessageAssistantContent(marker, payload),
            _ => new UnknownToolContent(marker, payload),
        });

    public static OpenCodeJsonContext Create()
    {
        var options = new JsonSerializerOptions(OpenCodeJsonContext.Default.Options) { DefaultBufferSize = 16 };
        options.Converters.Insert(0, new ToolUnionMetadataOracle<ISessionMessageToolState>());
        options.Converters.Insert(0, new ToolUnionMetadataOracle<IToolContent>());
        options.Converters.Insert(0, new ToolUnionMetadataOracle<IEvent>());
        options.Converters.Insert(0, new ToolUnionMetadataOracle<ISessionMessageInfo>());
        options.Converters.Insert(0, new ToolUnionMetadataOracle<ISessionMessageAssistantContent>());
        return new OpenCodeJsonContext(options);
    }

    private static Dictionary<string, Type?> EventTypes() => new(StringComparer.Ordinal)
    {
        ["agent.updated"] = typeof(AgentUpdated),
        ["artifact.updated"] = typeof(ArtifactUpdated),
        ["command.updated"] = typeof(CommandUpdated),
        ["config.updated"] = typeof(ConfigUpdated),
        ["credential.switched"] = typeof(CredentialSwitched),
        ["credential.updated"] = typeof(CredentialUpdated),
        ["experimental.mcp.event"] = typeof(ExperimentalMcpEvent),
        ["filesystem.changed"] = typeof(FilesystemChanged),
        ["form.cancelled"] = typeof(FormCancelled),
        ["form.created"] = typeof(FormCreated),
        ["form.replied"] = typeof(FormReplied),
        ["installation.update-available"] = typeof(InstallationUpdateAvailable),
        ["installation.updated"] = typeof(InstallationUpdated),
        ["integration.updated"] = typeof(IntegrationUpdated),
        ["location.shutdown"] = typeof(LocationShutdown),
        ["mcp.resources.changed"] = typeof(McpResourcesChanged),
        ["mcp.status.changed"] = typeof(McpStatusChanged),
        ["model.updated"] = typeof(ModelUpdated),
        ["models-dev.refreshed"] = typeof(ModelsDevRefreshed),
        ["permission.asked"] = typeof(PermissionAsked),
        ["permission.replied"] = typeof(PermissionReplied),
        ["persistent-pty.added"] = typeof(PersistentPtyAdded),
        ["persistent-pty.removed"] = typeof(PersistentPtyRemoved),
        ["persistent-pty.updated"] = typeof(PersistentPtyUpdated),
        ["plugin.updated"] = typeof(PluginUpdated),
        ["project.updated"] = typeof(ProjectUpdated),
        ["provider.updated"] = typeof(ProviderUpdated),
        ["pty.created"] = typeof(PtyCreated),
        ["pty.deleted"] = typeof(PtyDeleted),
        ["pty.exited"] = typeof(PtyExited),
        ["pty.updated"] = typeof(PtyUpdated),
        ["reference.updated"] = typeof(ReferenceUpdated),
        ["server.connected"] = typeof(EventServerConnected),
        ["shell.created"] = typeof(ShellCreated),
        ["shell.deleted"] = typeof(ShellDeleted),
        ["shell.exited"] = typeof(ShellExited),
        ["skill.updated"] = typeof(SkillUpdated),
        ["tui.command.execute"] = typeof(TuiCommandExecute),
        ["tui.prompt.append"] = typeof(TuiPromptAppend),
        ["tui.session.select"] = typeof(TuiSessionSelect),
        ["tui.toast.show"] = typeof(TuiToastShow),
        ["vcs.branch.updated"] = typeof(VcsBranchUpdated),
        ["websearch.updated"] = typeof(WebsearchUpdated),
        ["worktree.resolved"] = typeof(WorktreeResolved),
        ["worktree.updated"] = typeof(WorktreeUpdated),
    };

    private static Dictionary<string, Type?> SessionEventTypes() => new(StringComparer.Ordinal)
    {
        ["session.agent.selected"] = typeof(SessionAgentSelected),
        ["session.archived"] = typeof(SessionArchived),
        ["session.compaction.delta"] = typeof(SessionCompactionDelta),
        ["session.compaction.ended"] = typeof(SessionCompactionEnded),
        ["session.compaction.failed"] = typeof(SessionCompactionFailed),
        ["session.compaction.started"] = typeof(SessionCompactionStarted),
        ["session.created"] = typeof(SessionCreated),
        ["session.deleted"] = typeof(SessionDeleted),
        ["session.execution.failed"] = typeof(SessionExecutionFailed),
        ["session.execution.interrupted"] = typeof(SessionExecutionInterrupted),
        ["session.execution.started"] = typeof(SessionExecutionStarted),
        ["session.execution.succeeded"] = typeof(SessionExecutionSucceeded),
        ["session.forked"] = typeof(SessionForked),
        ["session.idle"] = typeof(SessionIdle),
        ["session.inbox.cancelled"] = typeof(SessionInboxCancelled),
        ["session.inbox.delivered"] = typeof(SessionInboxDelivered),
        ["session.inbox.delivery.changed"] = typeof(SessionInboxDeliveryChanged),
        ["session.inbox.enqueued"] = typeof(SessionInboxEnqueued),
        ["session.instructions.updated"] = typeof(SessionInstructionsUpdated),
        ["session.metadata.updated"] = typeof(SessionMetadataUpdated),
        ["session.model.selected"] = typeof(SessionModelSelected),
        ["session.moved"] = typeof(SessionMoved),
        ["session.permissions"] = typeof(SessionPermissions),
        ["session.reasoning.delta"] = typeof(SessionReasoningDelta),
        ["session.reasoning.ended"] = typeof(SessionReasoningEnded),
        ["session.reasoning.started"] = typeof(SessionReasoningStarted),
        ["session.renamed"] = typeof(SessionRenamed),
        ["session.retry.scheduled"] = typeof(SessionRetryScheduled),
        ["session.revert.cleared"] = typeof(SessionRevertCleared),
        ["session.revert.committed"] = typeof(SessionRevertCommitted),
        ["session.revert.staged"] = typeof(SessionRevertStaged),
        ["session.shell.ended"] = typeof(SessionShellEnded),
        ["session.shell.started"] = typeof(SessionShellStarted),
        ["session.skill.activated"] = typeof(SessionSkillActivated),
        ["session.status"] = typeof(SessionStatusUpdated),
        ["session.step.ended"] = typeof(SessionStepEnded),
        ["session.step.failed"] = typeof(SessionStepFailed),
        ["session.step.first-token"] = typeof(SessionStepFirstToken),
        ["session.step.started"] = typeof(SessionStepStarted),
        ["session.step.streamed"] = typeof(SessionStepStreamed),
        ["session.synthetic"] = typeof(SessionSynthetic),
        ["session.text.delta"] = typeof(SessionTextDelta),
        ["session.text.ended"] = typeof(SessionTextEnded),
        ["session.text.started"] = typeof(SessionTextStarted),
        ["session.tool.called"] = typeof(SessionToolCalled),
        ["session.tool.failed"] = typeof(SessionToolFailed),
        ["session.tool.input.delta"] = typeof(SessionToolInputDelta),
        ["session.tool.input.ended"] = typeof(SessionToolInputEnded),
        ["session.tool.input.started"] = typeof(SessionToolInputStarted),
        ["session.tool.progress"] = typeof(SessionToolProgress),
        ["session.tool.success"] = typeof(SessionToolSuccess),
        ["session.unarchived"] = typeof(SessionUnarchived),
        ["session.usage.updated"] = typeof(SessionUsageUpdated),
        ["session.viewed"] = typeof(SessionViewed),
    };
}
