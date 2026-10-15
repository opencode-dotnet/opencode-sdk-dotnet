namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// One analyzer config file as written: its repository-relative path, its full path, whether it is a
/// global config, the entries above its first section, and its sections in file order.
/// </summary>
internal sealed record AnalyzerConfigDocument(
    string Origin,
    string FullPath,
    string Text,
    bool IsGlobal,
    IReadOnlyList<AnalyzerConfigEntry> Preamble,
    IReadOnlyList<AnalyzerConfigSection> Sections);
