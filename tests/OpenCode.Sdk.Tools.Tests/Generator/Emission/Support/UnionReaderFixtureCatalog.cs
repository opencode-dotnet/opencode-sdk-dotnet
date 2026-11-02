using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal sealed class UnionReaderFixtureCatalog
{
    private readonly System.Reflection.Assembly _assembly = typeof(UnionReaderFixtureCatalog).Assembly;
    private const string Prefix = "OpenCode.Sdk.Tests.Fixtures.Serialization.";
    private static readonly IReadOnlyDictionary<string, string> Roots = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["duplicate-known-session-message-marker"] = "ISessionMessageInfo",
        ["duplicate-unknown-session-message-marker"] = "ISessionMessageInfo",
        ["empty-assistant-marker"] = "ISessionMessageAssistantContent",
        ["error-carrying-both-markers"] = "IOpenCodeError",
        ["first-match-corpus"] = "composite",
        ["json-element-dictionary-array-token"] = "SessionMessageToolStateCompleted",
        ["json-element-dictionary-duplicate-keys"] = "SessionMessageToolStateCompleted",
        ["json-element-dictionary-empty-object"] = "SessionMessageToolStateCompleted",
        ["json-element-dictionary-null-entry"] = "SessionMessageToolStateCompleted",
        ["json-element-dictionary-null-required"] = "SessionMessageToolStateCompleted",
        ["json-element-dictionary-string-token"] = "SessionMessageToolStateCompleted",
        ["json-element-dictionary-value-kinds"] = "SessionMessageToolStateCompleted",
        ["json-element-dictionary-write-expected"] = "SessionCreateRequest",
        ["json-element-dictionary-write-input"] = "SessionCreateRequest",
        ["known-assistant-content"] = "ISessionMessageAssistantContent",
        ["known-command-attempt-complete"] = "IIntegrationCommandAttemptStatus",
        ["known-compaction-message"] = "ISessionMessageInfo",
        ["known-diff-status"] = "FileDiffInfo",
        ["known-inbox-compaction"] = "ISessionInboxInfo",
        ["known-mcp-server"] = "McpServer",
        ["known-permission"] = "SessionPermissionCreateData",
        ["known-permission-request"] = "PermissionRequest",
        ["known-persistent-pty"] = "PersistentPtyInfo",
        ["known-persistent-pty-handoff"] = "PersistentPtyHandoff",
        ["known-persistent-pty-read"] = "PersistentPtyReadResult",
        ["known-persistent-pty-snapshot"] = "PersistentPtySnapshot",
        ["known-pty"] = "Pty",
        ["known-rpc-event"] = "IEvent",
        ["known-session"] = "SessionInfo",
        ["known-session-message"] = "ISessionMessageInfo",
        ["known-session-message-idle"] = "ISessionMessageInfo",
        ["known-session-message-shell"] = "ISessionMessageInfo",
        ["known-session-stats"] = "SessionStatsInfo",
        ["known-shell"] = "ShellInfo",
        ["known-tool-state"] = "ISessionMessageToolState",
        ["known-websearch-response"] = "WebSearchResponse",
        ["known-worktree-error"] = "IOpenCodeError",
        ["long-unknown-session-message-marker"] = "ISessionMessageInfo",
        ["malformed-assistant-marker"] = "ISessionMessageAssistantContent",
        ["mismatched-compaction-marker"] = "ISessionMessageCompaction",
        ["missing-assistant-marker"] = "ISessionMessageAssistantContent",
        ["non-object-union-payload"] = "ISessionMessageAssistantContent",
        ["null-diff-status"] = "FileDiffInfo",
        ["null-optional-collection"] = "ISessionMessageInfo",
        ["null-parent-session"] = "SessionInfo",
        ["numeric-diff-status"] = "FileDiffInfo",
        ["numeric-status-session"] = "SessionInfo",
        ["retry-status-with-string-attempt"] = "ISessionStatus",
        ["session-create-request-absent"] = "SessionCreateRequest",
        ["session-create-request-cleared"] = "SessionCreateRequest",
        ["session-create-request-set"] = "SessionCreateRequest",
        ["stream-events"] = "IEvent",
        ["stream-message-list-page"] = "ISessionMessageInfo",
        ["stream-session-messages"] = "ISessionMessageInfo",
        ["unknown-assistant-content"] = "ISessionMessageAssistantContent",
        ["unknown-compaction-status"] = "ISessionMessageCompaction",
        ["unknown-config-entry"] = "IConfigEntry",
        ["unknown-credential-value"] = "ICredentialValue",
        ["unknown-diff-status"] = "FileDiffInfo",
        ["unknown-error-with-different-marker"] = "IOpenCodeError",
        ["unknown-error-without-marker"] = "IOpenCodeError",
        ["unknown-interrupted-reason-event"] = "IEvent",
        ["unknown-mcp"] = "IMcpServerConfig",
        ["unknown-mcp-oauth-token"] = "McpOAuthConfig",
        ["unknown-mcp-status"] = "IMcpStatus",
        ["unknown-name-tagged-error"] = "IOpenCodeError",
        ["unknown-session-message"] = "ISessionMessageInfo",
        ["unknown-tool-state"] = "ISessionMessageToolState",
        ["worktree-error-null-force-required"] = "IOpenCodeError",
        ["worktree-error-without-force-required"] = "IOpenCodeError",
    };

    public static IReadOnlyDictionary<string, string> NonMaterialization { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["first-match-expected"] = "Expected arm indices annotate first-match-corpus; this is not an independent wire fixture.",
    };

    public static IEnumerable<string> ResourceNames => Roots.Keys.Concat(NonMaterialization.Keys).Select(static name => Prefix + name + ".json");

    public async IAsyncEnumerable<RecordCorpusRow> RowsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (name, root) in Roots)
        {
            await using var stream = _assembly.GetManifestResourceStream(Prefix + name + ".json")
                ?? throw new InvalidOperationException("Missing catalog resource: " + name);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var payload = name == "stream-message-list-page" ? document.RootElement.GetProperty("data") : document.RootElement;
            var values = payload.ValueKind == JsonValueKind.Array ? payload.EnumerateArray().ToArray() : [payload];
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                var type = root == "composite" ? value.GetProperty("union").GetString() switch
                {
                    "reference" => "ConfigReferenceEntry",
                    "lsp" => "ConfigLspEntry",
                    "migration" => "ExperimentalMigrationV1Status",
                    _ => throw new InvalidOperationException("Unknown first-match corpus root."),
                } : root;
                if (root == "composite")
                {
                    value = value.GetProperty("value");
                }
                yield return new(name + "/" + index.ToString(CultureInfo.InvariantCulture), type, null, Encoding.UTF8.GetBytes(value.GetRawText()));
            }
        }

        yield return new("benchmark/large-message", "ISessionMessageInfo", null,
            new AssistantMessageComposer(BenchmarkFixtures.DeepAssistantMessage()).WithContentParts(2400), true);
        for (var index = 0; index < 16 * 65; index++)
        {
            var id = "evt_bench" + (index + 1).ToString("D20", CultureInfo.InvariantCulture);
            var body = index % 65 == 64 ? BenchmarkFixtures.PromptTextEndedBody(id) : (index % 65 % 4) switch
            {
                0 or 1 => BenchmarkFixtures.PromptTextDeltaBody(id),
                2 => BenchmarkFixtures.PromptReasoningDeltaBody(id),
                _ => BenchmarkFixtures.PromptToolInputDeltaBody(id),
            };
            yield return new("benchmark/prompt/" + index.ToString(CultureInfo.InvariantCulture), "IEvent", null, Encoding.UTF8.GetBytes(body), true);
        }
    }
}
