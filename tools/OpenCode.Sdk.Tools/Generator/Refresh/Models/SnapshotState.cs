namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>One observation of the accepted files and checkout; null bytes mean an absent file.</summary>
internal sealed record SnapshotState(
    byte[] Document, byte[]? Receipt, byte[]? Markdown, byte[]? SourceWatch, string Checkout);
