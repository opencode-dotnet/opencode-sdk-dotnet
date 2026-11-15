using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>One watched upstream file's hash and per-behavior anchor verdicts at the prepared commit.</summary>
internal sealed record ReceiptWatchedSource
{
    /// <summary>Gets the submodule-relative path of the watched file.</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary>Gets the SHA-256 of the file's blob at the observed commit.</summary>
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }

    /// <summary>Gets each behavior's anchor text and verdict, in watch order.</summary>
    [JsonPropertyName("anchors")]
    public required IReadOnlyList<ReceiptWatchedAnchor> Anchors
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
