namespace OpenCode.Sdk.Tools.TestRoster.Models;

/// <summary>One TRX results file: its module identity, parsed from the file name, and its counters.</summary>
internal sealed record TestRunSummary(
    string FileName,
    string Assembly,
    string TargetFramework,
    string Arch,
    int Total,
    int Executed)
{
    /// <summary>The module key the roster uses: "Assembly/target-framework".</summary>
    public string Module => $"{Assembly}/{TargetFramework}";
}
