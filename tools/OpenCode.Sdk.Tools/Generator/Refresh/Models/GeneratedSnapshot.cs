namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>The generated document and its unpatched baseline and patch evidence travel together.</summary>
internal sealed record GeneratedSnapshot(byte[] NormalizedBytes, string BaselineSha, IReadOnlyList<ReceiptPatch> Patches);
