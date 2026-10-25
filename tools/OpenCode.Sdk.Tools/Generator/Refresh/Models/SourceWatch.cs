using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// The committed source watch (<c>spec/source-watch.json</c>): the upstream files the
/// hand-written doors depend on, pinned by hash and anchor. It is a review trigger over a
/// refresh, never a generation input — generation reads the accepted OpenAPI document alone
/// (ADR-0013).
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
