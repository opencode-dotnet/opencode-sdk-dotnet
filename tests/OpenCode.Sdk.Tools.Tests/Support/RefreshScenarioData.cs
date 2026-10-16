using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>Builders for synchronizer scenarios: documents, manifests, and receipts without raw dumps.</summary>
internal static class RefreshScenarioData
{
    /// <summary>A full commit SHA usable wherever a scenario needs one.</summary>
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public static WatchedSource TicketSource { get; } =
        Watched("packages/core/src/pty/ticket.ts", "const DEFAULT_TTL = Duration.seconds(60)\n", "seconds(60)");

    public static WatchedSource PtyBufferSource { get; } =
        Watched("packages/core/src/pty.ts", "const BUFFER_LIMIT = 1024 * 1024 * 2", "BUFFER_LIMIT");

    /// <summary>The managed SNAPSHOT.md identity: table, date and release/install prose.</summary>
    public const string SnapshotMarkdown = """
                                           # OpenAPI Snapshot

                                           Date: 2026-08-13

                                           | Fact | Value |
                                           |---|---|
                                           | Upstream file | `packages/protocol/openapi.json` |
                                           | Upstream release tag | `v2.0.29` |
                                           | Commit | aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa |
                                           | Upstream product channel | npm `@opencode/cli@latest` (channel `latest`); this tag published as `2.0.29`, installing the `opencode` command (plus the transitional `opencode2` alias) |

                                           semver release tags (`v2.0.0`…). This pin is the release tag `v2.0.29`, so
                                           `npm install -g @opencode/cli@2.0.29` installs a server built from exactly this commit; later
                                           """;

    /// <summary>Builds an OpenAPI document's bytes through the canonical spec builder.</summary>
    public static byte[] DocumentBytes(Action<SpecDocumentBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SpecDocumentBuilder();
        configure(builder);
        return Encoding.UTF8.GetBytes(builder.BuildJson());
    }

    public static PatchManifest Manifest(string patch = "001-test.patch", string sha256 = "", int order = 1,
        string keyword = "contentSchema", params string[] components) =>
        new()
        {
            Patch = patch,
            Sha256 = sha256,
            UpstreamReport = "https://github.com/anomalyco/opencode/pull/45182",
            Order = order,
            Touches = ["packages/protocol/script/generate-openapi.ts"],
            RepairPredicate = new PatchPredicate
            {
                Type = PatchPredicate.ComponentLacksKeyword,
                Components = components.Length is 0 ? ["V2EventEncoded"] : components,
                Keyword = keyword,
            },
            Retirement = "Raw upstream carries the keyword.",
        };

    /// <summary>Builds one watched upstream source pinned to the given content's hash.</summary>
    public static WatchedSource Watched(string path, string content, string anchor, string anchorType = SourceAnchor.Contains)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new WatchedSource
        {
            Path = path,
            Sha256 = DocumentInspector.Sha256Hex(Encoding.UTF8.GetBytes(content)),
            Behavior = $"the door's dependency on {anchor}",
            Anchor = new SourceAnchor { Type = anchorType, Text = anchor },
        };
    }

    /// <summary>Builds a source watch over the given entries.</summary>
    public static SourceWatch Watch(int schemaVersion, params WatchedSource[] sources) =>
        new() { SchemaVersion = schemaVersion, Sources = sources };

    public static ReceiptWatchedSource Observation(WatchedSource source, string sha256, bool anchorMatched) =>
        new() { Path = source.Path, Sha256 = sha256, AnchorMatched = anchorMatched };

    public static IReadOnlyList<ReceiptWatchedSource> OtherSources(WatchedSource source, string variation)
    {
        var matching = Observation(source, source.Sha256, anchorMatched: true);
        var other = matching with { Path = "other.ts" };
        return variation switch
        {
            "fewer" => [],
            "more" => [matching, other],
            _ => [other],
        };
    }

    public static SnapshotReceipt Receipt(byte[] document, string? path = null)
    {
        var stats = DocumentInspector.Inspect(document);
        return new SnapshotReceipt
        {
            SchemaVersion = 2,
            ReleaseTag = "v2.0.30",
            UpstreamCommit = Commit,
            RawDocumentSha256 = DocumentInspector.Sha256Hex(document),
            GeneratedBaselineSha256 = DocumentInspector.Sha256Hex(document),
            Patches = [],
            NormalizedDocumentSha256 = DocumentInspector.Sha256Hex(document),
            NormalizedDocumentPath = path,
            OperationSetDigest = stats.OperationSetDigest,
            OperationCount = stats.OperationIds.Count,
            ComponentCount = stats.ComponentCount,
            ContentSchemaCount = stats.ContentSchemaCount,
            AddedOperations = [],
            RemovedOperations = [],
        };
    }

    public static SnapshotReceipt WithStatistic(SnapshotReceipt receipt, string variation) => variation switch
    {
        "hash" => receipt with { NormalizedDocumentSha256 = "changed" },
        "digest" => receipt with { OperationSetDigest = "changed" },
        "operations" => receipt with { OperationCount = 2 },
        "components" => receipt with { ComponentCount = 2 },
        "content" => receipt with { ContentSchemaCount = 2 },
        _ => receipt,
    };

    public static string WithNullMember(string json, string member)
    {
        var document = JsonNode.Parse(json)!;
        document[member] = null;
        return document.ToJsonString();
    }

    public static string Serialize(PatchManifest manifest) => JsonSerializer.Serialize(manifest, ToolJsonContext.Default.PatchManifest);

    public static string Serialize(SourceWatch watch) => JsonSerializer.Serialize(watch, ToolJsonContext.Default.SourceWatch);

    public static string Serialize(SnapshotReceipt receipt) => JsonSerializer.Serialize(receipt, ToolJsonContext.Default.SnapshotReceipt);
}
