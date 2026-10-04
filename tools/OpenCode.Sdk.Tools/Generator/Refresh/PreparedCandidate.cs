using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// Everything one prepare run resolved before the receipt was written: the exact commit, the
/// scratch directory the artifacts land in, the upstream documents, the applied patch record,
/// and the watched-source observations.
/// </summary>
internal sealed record PreparedCandidate
{
    public required string Commit { get; init; }

    public required string ScratchDirectory { get; init; }

    /// <summary>Gets upstream's committed artifact at the candidate commit.</summary>
    public required byte[] RawBytes { get; init; }

    /// <summary>Gets the SHA-256 of the unpatched generator run.</summary>
    public required string BaselineSha { get; init; }

    /// <summary>Gets the document apply would install as the accepted snapshot.</summary>
    public required byte[] NormalizedBytes { get; init; }

    public required IReadOnlyList<ReceiptPatch> Patches { get; init; }

    public required IReadOnlyList<ReceiptWatchedSource> WatchedSources { get; init; }
}
