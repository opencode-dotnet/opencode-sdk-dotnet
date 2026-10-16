namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>Fully validated payloads; the committer has no candidate path or policy to re-read.</summary>
internal sealed record SnapshotApplyPlan(
    SnapshotReceipt Receipt, byte[] Document, string Markdown, string? SourceWatch, string ReceiptText);
