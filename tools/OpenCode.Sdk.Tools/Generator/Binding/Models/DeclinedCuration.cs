using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Declines one operation the generator cannot reach: a standing wall refuses it and the
/// maintainer has decided the wall stands rather than be worked around. The operation is never
/// selected, so the reason is the only record of why the released surface omits it — it states
/// the wall as present-tense fact and names the decision that let it stand.
/// </summary>
internal sealed record DeclinedCuration : IReasonedRow
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
