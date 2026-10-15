namespace OpenCode.Sdk.Tools.Clones;

/// <summary>
/// One area the clone gate scans: its name, the repository-relative path it scans, which manifest
/// side it leaves out, any further globs it ignores, the committed baseline that gates it (none for a
/// scope that only reports), and the duplicated-lines percentage it may not exceed (none for no limit).
/// </summary>
internal sealed record CloneScope(
    string Name,
    string Path,
    CloneScopeFilter Filter,
    IReadOnlyList<string> Ignore,
    string? Baseline,
    int? Threshold)
{
    /// <summary>Gets whether new clones in this scope fail the gate.</summary>
    public bool IsGated => Baseline is not null;
}
