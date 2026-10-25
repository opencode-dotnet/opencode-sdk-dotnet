using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// The committed declaration of one Restore patch (ADR-0020): its exact bytes, upstream report,
/// ordered position, touched files, and the predicates that force its retirement.
/// </summary>
internal sealed record PatchManifest
{
    /// <summary>Gets the patch file name, resolved beside the manifest under <c>spec/patches/</c>.</summary>
    [JsonPropertyName("patch")] public required string Patch { get; init; }

    /// <summary>Gets the required SHA-256 of the patch file.</summary>
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }

    /// <summary>Gets the upstream issue or pull request this patch temporarily stands in for.</summary>
    [JsonPropertyName("upstreamReport")] public required string UpstreamReport { get; init; }

    /// <summary>Gets the ordered position within the patch list.</summary>
    [JsonPropertyName("order")] public required int Order { get; init; }

    /// <summary>Gets the upstream-relative paths the patch touches; their preimages ride the receipt.</summary>
    [JsonPropertyName("touches")]
    public required IReadOnlyList<string> Touches
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>
    /// Gets the repair predicate evaluated against the raw upstream document: while it holds the
    /// repair is still needed; once raw upstream satisfies the repair, prepare refuses the patch
    /// and forces an empty-patch retirement refresh.
    /// </summary>
    [JsonPropertyName("repairPredicate")] public required PatchPredicate RepairPredicate { get; init; }

    /// <summary>Gets the human-readable retirement condition.</summary>
    [JsonPropertyName("retirement")] public required string Retirement { get; init; }
}
