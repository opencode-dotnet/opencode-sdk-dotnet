using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// One area of the Sessions family's models: every Sessions model whose name one of
/// <see cref="Matches"/> prefixes at a word boundary lives in the <see cref="Area"/> subfolder.
/// A Sessions model no row matches stays in the family's model folder.
/// </summary>
internal sealed record SessionAreaCuration : IReasonedRow
{
    [JsonPropertyName("area")] public required string Area { get; init; }

    [JsonPropertyName("matches")]
    public required IReadOnlyList<string> Matches
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
