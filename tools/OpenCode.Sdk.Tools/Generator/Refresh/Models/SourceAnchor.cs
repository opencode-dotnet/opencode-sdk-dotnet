using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// A machine-checkable content anchor over one watched upstream source file. The single
/// supported kind, <c>contains</c>, holds while the file still carries the literal that names
/// the upstream behavior this SDK or opencode-agents-ai depends on; once it stops holding, its owner needs
/// a human reading before the pin moves.
/// </summary>
internal sealed record SourceAnchor
{
    /// <summary>The only supported anchor kind today.</summary>
    public const string Contains = "contains";

    [JsonPropertyName("type")] public required string Type { get; init; }

    /// <summary>Gets the literal the watched file must still carry.</summary>
    [JsonPropertyName("text")] public required string Text { get; init; }
}
