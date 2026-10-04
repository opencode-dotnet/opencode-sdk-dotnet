using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Masks one open model's extension data in its printed form. The members the pinned
/// document leaves open have no wire name a <see cref="RedactedMemberCuration"/> row could select,
/// and an open bag carries a secret only where upstream reads one from it, so each masked bag is
/// its own row. The reason states which secrets the open members carry.
/// </summary>
internal sealed record RedactedOpenMembersCuration
{
    /// <summary>Gets the generated model's C# type name.</summary>
    [JsonPropertyName("model")] public required string Model { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
