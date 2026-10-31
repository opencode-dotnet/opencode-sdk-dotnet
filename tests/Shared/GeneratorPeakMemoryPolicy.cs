namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Selects generator cases and readable peak-memory metrics.</summary>
internal static class GeneratorPeakMemoryPolicy
{
    public static bool ShouldMeasure(string? benchmarkNamespace) =>
        string.Equals(benchmarkNamespace, "OpenCode.Sdk.Performance.Tests.Generator", StringComparison.Ordinal);

    public static long? MetricValue(long? peakBytes) => peakBytes > 0 ? peakBytes : null;
}
