namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>One <c>key = value</c> line of an analyzer config, with its 1-based line number.</summary>
internal sealed record AnalyzerConfigEntry(string Key, string Value, int Line);
