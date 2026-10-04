using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// The immutable record of one prepared snapshot candidate (ADR-0020): the exact inputs, hashes,
/// patches, and invariants a human reviews before apply installs the accepted snapshot.
/// </summary>
internal sealed record SnapshotReceipt
{
    [JsonPropertyName("schemaVersion")] public required int SchemaVersion { get; init; }

    /// <summary>Gets the full upstream commit SHA the candidate was produced from.</summary>
    [JsonPropertyName("upstreamCommit")] public required string UpstreamCommit { get; init; }

    /// <summary>Gets the SHA-256 of upstream's committed artifact at that commit.</summary>
    [JsonPropertyName("rawDocumentSha256")] public required string RawDocumentSha256 { get; init; }

    /// <summary>
    /// Gets the SHA-256 of the document the pinned upstream generator produces at that commit with no
    /// patch applied. It differs from <see cref="RawDocumentSha256"/> when upstream committed a stale
    /// artifact; the accepted snapshot never consumes the committed artifact.
    /// </summary>
    [JsonPropertyName("generatedBaselineSha256")] public required string GeneratedBaselineSha256 { get; init; }

    [JsonPropertyName("patches")]
    public required IReadOnlyList<ReceiptPatch> Patches
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<ReceiptPatch>());

    /// <summary>Gets the SHA-256 of the normalized document apply installs as the accepted snapshot.</summary>
    [JsonPropertyName("normalizedDocumentSha256")] public required string NormalizedDocumentSha256 { get; init; }

    /// <summary>Gets where prepare wrote the normalized document; scrubbed once the receipt is applied.</summary>
    [JsonPropertyName("normalizedDocumentPath")] public string? NormalizedDocumentPath { get; init; }

    /// <summary>Gets the SHA-256 over the sorted, newline-joined operation ids.</summary>
    [JsonPropertyName("operationSetDigest")] public required string OperationSetDigest { get; init; }

    [JsonPropertyName("operationCount")] public required int OperationCount { get; init; }

    [JsonPropertyName("addedOperations")]
    public required IReadOnlyList<string> AddedOperations
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    [JsonPropertyName("removedOperations")]
    public required IReadOnlyList<string> RemovedOperations
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    [JsonPropertyName("componentCount")] public required int ComponentCount { get; init; }

    /// <summary>Gets the document-wide <c>contentSchema</c> occurrence count — the #56 stream-link signal.</summary>
    [JsonPropertyName("contentSchemaCount")] public required int ContentSchemaCount { get; init; }

    /// <summary>
    /// Gets the watched upstream sources as prepare observed them at the candidate commit: the
    /// hand-written doors' inputs, hashed, with each anchor's verdict. A review trigger the
    /// human reads beside the pins in <c>spec/source-watch.json</c>, never a generation input.
    /// </summary>
    [JsonPropertyName("watchedSources")]
    public IReadOnlyList<ReceiptWatchedSource> WatchedSources
    {
        get;

        // Unlike its required siblings this member is optional: a receipt written before the
        // source watch existed carries none, and the deserializer hands the creator a null for
        // it. An absent watch is an empty one, never a null list.
        init => field = value is null ? Array.AsReadOnly(Array.Empty<ReceiptWatchedSource>()) : Array.AsReadOnly([.. value]);
    } = Array.AsReadOnly(Array.Empty<ReceiptWatchedSource>());
}
