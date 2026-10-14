using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Places the models a name prefix cannot place. <see cref="Match"/> is a name prefix matched at a
/// word boundary, so one row can place a whole name group; <see cref="Owner"/> is an admitted
/// family or <c>Common</c>, the shared home of the cross-cutting error spine.
/// </summary>
internal sealed record ModelOwnerCuration
{
    [JsonPropertyName("match")] public required string Match { get; init; }

    [JsonPropertyName("owner")] public required string Owner { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
