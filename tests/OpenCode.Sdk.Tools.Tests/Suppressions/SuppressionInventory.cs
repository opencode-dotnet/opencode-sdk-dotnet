namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// What one scan of the repository found: its root, every repository file by relative path, every
/// suppression, every analyzer config as written, and which C# files each project compiles.
/// </summary>
internal sealed record SuppressionInventory(
    string Root,
    IReadOnlyList<string> Files,
    IReadOnlyList<Suppression> Suppressions,
    IReadOnlyList<AnalyzerConfigDocument> AnalyzerConfigs,
    ProjectMembership Projects);
