using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// A judgement on one open model's printed extension data. The members the pinned document leaves
/// open have no wire name a <see cref="RedactedMemberCuration"/> row could select, so every open
/// model is decided by its own row: <c>redact: true</c> masks the bag, <c>redact: false</c> prints
/// it as the compiler would. The reason states which secrets the open members carry, or why they
/// carry none.
/// </summary>
internal sealed record RedactedOpenMembersCuration
{
    /// <summary>Gets the generated model's C# type name.</summary>
    [JsonPropertyName("model")] public required string Model { get; init; }

    [JsonPropertyName("redact")] public required bool Redact { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
