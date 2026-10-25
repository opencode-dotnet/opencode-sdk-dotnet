namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Fails a benchmark process if any summary is incomplete or unsuccessful.</summary>
internal static class BenchmarkExitPolicy
{
    public static int GetExitCode(IEnumerable<SummaryResult> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        return summaries.Any(static summary => summary.HasValidationErrors || summary.HasBuildFailures
            || summary.MissingReports != 0 || summary.UnsuccessfulReports != 0 || summary.MissingStatistics != 0) ? 1 : 0;
    }

    internal sealed record SummaryResult(
        bool HasValidationErrors,
        bool HasBuildFailures,
        int MissingReports,
        int UnsuccessfulReports,
        int MissingStatistics);
}
