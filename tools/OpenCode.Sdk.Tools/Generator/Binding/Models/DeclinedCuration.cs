using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Declines one operation the generator does not generate: a standing wall refuses it and the
/// maintainer has decided the wall stands rather than be worked around, or both of upstream's
/// first-party clients omit it. The operation is never selected, so the reason is the only record
/// of why the released surface omits it — it states the wall or the omission as present-tense fact
/// and names the decision that let it stand.
/// </summary>
internal sealed record DeclinedCuration : IReasonedRow
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }

    /// <summary>
    /// The endpoint key both of upstream's first-party client omit lists carry, when that omission
    /// rather than a wall keeps the operation out; the bindability probe then exempts the row.
    /// </summary>
    [JsonPropertyName("upstreamOmitted")] public string? UpstreamOmitted { get; init; }
}
