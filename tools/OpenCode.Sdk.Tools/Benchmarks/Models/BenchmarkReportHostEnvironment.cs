namespace OpenCode.Sdk.Tools.Benchmarks.Models;

/// <summary>Consumed slice of an export's host description; older exports omit members or all of it.</summary>
internal sealed record BenchmarkReportHostEnvironment
{
    public string? BenchmarkDotNetVersion { get; init; }

    public string? OsVersion { get; init; }

    public string? ProcessorName { get; init; }

    public string? DotNetCliVersion { get; init; }
}
