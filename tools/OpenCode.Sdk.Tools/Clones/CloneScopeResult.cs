namespace OpenCode.Sdk.Tools.Clones;

/// <summary>
/// One scope's run: the detector's exit code, the totals of its report (clones, duplicated lines,
/// scanned lines, the duplicated percentage, and the clones its baseline does not hold), and what
/// it wrote to standard error.
/// </summary>
internal sealed record CloneScopeResult(
    CloneScope Scope,
    int ExitCode,
    int Clones,
    int DuplicatedLines,
    int Lines,
    double Percentage,
    int NewClones,
    string StandardError);
