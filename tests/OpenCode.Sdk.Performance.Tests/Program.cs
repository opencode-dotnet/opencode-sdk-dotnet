using BenchmarkDotNet.Running;
using OpenCode.Sdk.Performance.Tests.Macro;
using OpenCode.Sdk.Performance.Tests.Support;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Performance.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--macro", "pty-receive-queue", ..])
        {
            return await new PtyReceiveQueueMacro().RunAsync(args).ConfigureAwait(false);
        }

        var summaries = await BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).RunAsync(args, BenchmarkConfig.Create()).ConfigureAwait(false);
        return BenchmarkExitPolicy.GetExitCode(summaries.Select(static summary => new BenchmarkExitPolicy.SummaryResult(
            summary.ValidationErrors.Any(),
            summary.Reports.Any(static report => !report.BuildResult.IsBuildSuccess),
            summary.BenchmarksCases.Count(benchmarkCase => !summary.HasReport(benchmarkCase)),
            summary.Reports.Count(static report => !report.Success),
            summary.Reports.Count(static report => report.ResultStatistics is null),
            summary.BenchmarksCases.Length)));
    }
}
