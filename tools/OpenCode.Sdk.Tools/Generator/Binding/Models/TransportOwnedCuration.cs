using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Pins a SHA-256 fingerprint over one transport-owned operation's ingested wire subtree
/// (method, path, parameters, the <c>x-websocket</c> marker, request body, and declared
/// responses). The operation is never selected into the generation profile — a hand-written
/// door depends on its shape instead — so this is the only generation-time check standing
/// between a spec refresh and silent drift on that door.
/// </summary>
internal sealed record TransportOwnedCuration : IReasonedRow
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    [JsonPropertyName("subtreeSha256")] public required string SubtreeSha256 { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
