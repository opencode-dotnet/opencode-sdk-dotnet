using System.Diagnostics;
using OpenCode.Sdk.Tools.Benchmarks.Models;

namespace OpenCode.Sdk.Tools.Benchmarks;

/// <summary>Joins two benchmark runs case-by-case on full name and runtime leg.</summary>
internal static class BenchmarkComparisonComposer
{
    public static BenchmarkComparison Compose(IReadOnlyList<BenchmarkRunCase> before, IReadOnlyList<BenchmarkRunCase> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeByCase = before.ToDictionary(static runCase => (runCase.FullName, runCase.Runtime));
        var rows = new List<BenchmarkComparisonRow>();
        var afterOnly = new List<BenchmarkRunCase>();
        var matchedCases = new HashSet<(string FullName, string Runtime)>();
        foreach (var afterCase in after)
        {
            if (!beforeByCase.TryGetValue((afterCase.FullName, afterCase.Runtime), out var beforeCase))
            {
                afterOnly.Add(afterCase);
                continue;
            }

            matchedCases.Add((afterCase.FullName, afterCase.Runtime));

            // The wire figures describe the case's fixture, not a measurement: prefer the after
            // leg's triple and fall back to the baseline's when only it recorded wire metrics.
            var wireSource = afterCase.HasWireMetrics ? afterCase : beforeCase;
            // The command refuses NA runs before composition; every remaining case has memory diagnostics.
            Debug.Assert(beforeCase.AllocatedBytes.HasValue);
            Debug.Assert(afterCase.AllocatedBytes.HasValue);
            rows.Add(new BenchmarkComparisonRow
            {
                CaseLabel = afterCase.CaseLabel,
                Runtime = afterCase.Runtime,
                AllocatedBefore = beforeCase.AllocatedBytes.Value,
                AllocatedAfter = afterCase.AllocatedBytes.Value,
                AllocatedDelta = afterCase.AllocatedBytes.Value - beforeCase.AllocatedBytes.Value,
                TimeRatio = ComputeTimeRatio(beforeCase.MedianNanoseconds, afterCase.MedianNanoseconds),
                WireBytes = wireSource.WireBytes,
                WireItems = wireSource.WireItems,
                PayloadBytesPerItem = wireSource.PayloadBytesPerItem,
            });
        }

        var beforeOnly = before.Where(runCase => !matchedCases.Contains((runCase.FullName, runCase.Runtime)));
        return new BenchmarkComparison
        {
            Rows = [.. rows
                .OrderBy(static row => row.CaseLabel, StringComparer.Ordinal)
                .ThenBy(static row => row.Runtime, StringComparer.Ordinal)],
            BeforeOnly = Sort(beforeOnly),
            AfterOnly = Sort(afterOnly),
        };
    }

    /// <summary>A ratio needs a positive median on both legs; a noise-floor leg supplies none, and
    /// dividing by its zero would fabricate an infinite ratio.</summary>
    private static double? ComputeTimeRatio(double? beforeMedian, double? afterMedian) =>
        beforeMedian is { } beforeValue && afterMedian is { } afterValue ? afterValue / beforeValue : null;

    private static IReadOnlyList<BenchmarkRunCase> Sort(IEnumerable<BenchmarkRunCase> cases) =>
    [
        .. cases
            .OrderBy(static runCase => runCase.CaseLabel, StringComparer.Ordinal)
            .ThenBy(static runCase => runCase.Runtime, StringComparer.Ordinal),
    ];
}
