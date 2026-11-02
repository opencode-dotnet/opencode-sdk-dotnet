namespace OpenCode.Sdk.Tools.Benchmarks.Models;

/// <summary>Consumed slice of a BenchmarkDotNet <c>*-report-full.json</c> export.</summary>
internal sealed record BenchmarkReportDocument
{
    public BenchmarkReportHostEnvironment? HostEnvironmentInfo { get; init; }

    public IReadOnlyList<BenchmarkReportCase>? Benchmarks { get; init; }
}
