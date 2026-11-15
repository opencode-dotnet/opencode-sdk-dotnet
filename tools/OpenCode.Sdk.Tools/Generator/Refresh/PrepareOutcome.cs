using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>The scratch artifacts one prepare run produced.</summary>
internal sealed record PrepareOutcome
{
    public required SnapshotReceipt Receipt { get; init; }

    public required string ReceiptPath { get; init; }

    public required string NormalizedDocumentPath { get; init; }

    public required IReadOnlyList<WatchedSource> PinnedSources { get; init; }
}
