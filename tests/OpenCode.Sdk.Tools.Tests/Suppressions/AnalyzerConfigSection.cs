namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>One bracketed section of an analyzer config: its glob as written, its header's 1-based line, and its entries.</summary>
internal sealed record AnalyzerConfigSection(string Name, int Line, IReadOnlyList<AnalyzerConfigEntry> Entries);
