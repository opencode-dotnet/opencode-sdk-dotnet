using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// The committed source watch (<c>spec/source-watch.json</c>): upstream files this SDK or
/// opencode-agents-ai depends on, pinned by hash and owned behavior anchors. A refresh-time
/// review trigger; generation reads it only to check query parameter type evidence paths.
/// </summary>
internal sealed record SourceWatch
{
    [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<WatchedSource> Sources
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
