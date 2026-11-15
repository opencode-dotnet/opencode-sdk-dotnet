using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// One pinned upstream file and the behaviors this SDK or opencode-agents-ai depends on in it:
/// its submodule-relative path, blob hash, and each behavior's owner and content anchor.
/// </summary>
internal sealed record WatchedSource
{
    /// <summary>Gets the submodule-relative path of the watched file.</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary>Gets the SHA-256 of the file's blob at the accepted upstream commit.</summary>
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }

    /// <summary>Gets the owned behaviors and their content anchors, in watch order.</summary>
    [JsonPropertyName("behaviors")]
    public required IReadOnlyList<WatchedBehavior> Behaviors
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
