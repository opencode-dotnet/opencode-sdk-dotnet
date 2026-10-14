namespace OpenCode.Sdk.Tools.TestRoster.Models;

/// <summary>The outcome of comparing one roster leg against the TRX summaries of its results
/// directory.</summary>
internal sealed record TestRosterComparison(IReadOnlyList<TestRosterRow> Rows, IReadOnlyList<string> Failures)
{
    /// <summary>True when every roster entry matched one executed TRX and nothing unexpected ran.</summary>
    public bool Passed => Failures.Count == 0;
}
