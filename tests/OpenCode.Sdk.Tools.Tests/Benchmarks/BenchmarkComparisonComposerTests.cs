using OpenCode.Sdk.Tools.Benchmarks;
using OpenCode.Sdk.Tools.Benchmarks.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Benchmarks;

public sealed class BenchmarkComparisonComposerTests
{
    [Test]
    public async Task Compose_Should_Pair_Cases_On_Full_Name_And_Runtime()
    {
        var before = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2104, medianNanoseconds: 1000.0),
            Case("GetHealthAsync", ".NET Framework 4.7.2", allocatedBytes: 6432, medianNanoseconds: 16000.0),
        };
        var after = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2376, medianNanoseconds: 250.0),
        };

        var comparison = BenchmarkComparisonComposer.Compose(before, after);

        var row = comparison.Rows.Single();
        await Assert.That(row.CaseLabel).IsEqualTo("Health/GetHealthAsync [Fixture=health]");
        await Assert.That(row.Runtime).IsEqualTo(".NET 10.0");
        await Assert.That(row.AllocatedBefore).IsEqualTo(2104L);
        await Assert.That(row.AllocatedAfter).IsEqualTo(2376L);
        await Assert.That(row.AllocatedDelta).IsEqualTo(272L);
        await Assert.That(row.TimeRatio).IsEqualTo(0.25);
    }

    [Test]
    public async Task Compose_Should_Report_Cases_Missing_From_Either_Side()
    {
        var before = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2104, medianNanoseconds: 1000.0),
            Case("Deserialize", ".NET 10.0", allocatedBytes: 256, medianNanoseconds: 400.0),
        };
        var after = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2376, medianNanoseconds: 980.0),
            Case("AdaptSuccess", ".NET 10.0", allocatedBytes: 304, medianNanoseconds: 420.0),
        };

        var comparison = BenchmarkComparisonComposer.Compose(before, after);

        await Assert.That(comparison.Rows.Count).IsEqualTo(1);
        await Assert.That(comparison.BeforeOnly.Single().Method).IsEqualTo("Deserialize");
        await Assert.That(comparison.AfterOnly.Single().Method).IsEqualTo("AdaptSuccess");
    }

    [Test]
    public async Task Compose_Should_Order_Rows_By_Case_Label_Then_Runtime()
    {
        var before = new[]
        {
            Case("GetHealthAsync", ".NET Framework 4.7.2", allocatedBytes: 6432, medianNanoseconds: 16000.0),
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2104, medianNanoseconds: 1000.0),
            Case("AdaptSuccess", ".NET 10.0", allocatedBytes: 304, medianNanoseconds: 420.0),
        };
        var after = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2376, medianNanoseconds: 980.0),
            Case("AdaptSuccess", ".NET 10.0", allocatedBytes: 304, medianNanoseconds: 410.0),
            Case("GetHealthAsync", ".NET Framework 4.7.2", allocatedBytes: 4686, medianNanoseconds: 6000.0),
        };

        var comparison = BenchmarkComparisonComposer.Compose(before, after);

        var order = comparison.Rows.Select(row => $"{row.CaseLabel}|{row.Runtime}").ToArray();
        await Assert.That(order).IsEquivalentTo(
            [
                "Health/AdaptSuccess [Fixture=health]|.NET 10.0",
                "Health/GetHealthAsync [Fixture=health]|.NET 10.0",
                "Health/GetHealthAsync [Fixture=health]|.NET Framework 4.7.2",
            ],
            StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Compose_Should_Compare_Allocation_Without_A_Time_Ratio_When_A_Leg_Carries_No_Timing()
    {
        var before = new[]
        {
            Case("ConstantRoute", ".NET Framework 4.7.2", allocatedBytes: 24, medianNanoseconds: 12.5),
        };
        var after = new[]
        {
            Case("ConstantRoute", ".NET Framework 4.7.2", allocatedBytes: 0, medianNanoseconds: null),
        };

        var comparison = BenchmarkComparisonComposer.Compose(before, after);

        var row = comparison.Rows.Single();
        await Assert.That(row.AllocatedBefore).IsEqualTo(24L);
        await Assert.That(row.AllocatedAfter).IsEqualTo(0L);
        await Assert.That(row.AllocatedDelta).IsEqualTo(-24L);
        await Assert.That(row.TimeRatio).IsNull();
    }

    [Test]
    public async Task Compose_Should_Carry_The_After_Legs_Wire_Figures_When_Both_Runs_Record_Them()
    {
        var before = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2104, medianNanoseconds: 1000.0, wireBytes: 47, wireItems: 1, payloadBytesPerItem: 47),
        };
        var after = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2376, medianNanoseconds: 980.0, wireBytes: 49, wireItems: 1, payloadBytesPerItem: 49),
        };

        var comparison = BenchmarkComparisonComposer.Compose(before, after);

        var row = comparison.Rows.Single();
        await Assert.That(row.WireBytes).IsEqualTo(49L);
        await Assert.That(row.WireItems).IsEqualTo(1L);
        await Assert.That(row.PayloadBytesPerItem).IsEqualTo(49L);
    }

    [Test]
    public async Task Compose_Should_Fall_Back_To_The_Baseline_Wire_Figures_When_The_After_Run_Lacks_Them()
    {
        var before = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2104, medianNanoseconds: 1000.0, wireBytes: 49, wireItems: 1, payloadBytesPerItem: 49),
        };
        var after = new[]
        {
            Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2376, medianNanoseconds: 980.0),
        };

        var comparison = BenchmarkComparisonComposer.Compose(before, after);

        var row = comparison.Rows.Single();
        await Assert.That(row.WireBytes).IsEqualTo(49L);
        await Assert.That(row.WireItems).IsEqualTo(1L);
        await Assert.That(row.PayloadBytesPerItem).IsEqualTo(49L);
    }

    [Test]
    public async Task Compose_Should_Leave_The_Wire_Figures_Unset_When_Neither_Run_Records_Them()
    {
        var before = new[] { Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2104, medianNanoseconds: 1000.0) };
        var after = new[] { Case("GetHealthAsync", ".NET 10.0", allocatedBytes: 2376, medianNanoseconds: 980.0) };

        var comparison = BenchmarkComparisonComposer.Compose(before, after);

        var row = comparison.Rows.Single();
        await Assert.That(row.WireBytes).IsNull();
        await Assert.That(row.WireItems).IsNull();
        await Assert.That(row.PayloadBytesPerItem).IsNull();
    }

    private static BenchmarkRunCase Case(
        string method,
        string runtime,
        long allocatedBytes,
        double? medianNanoseconds,
        long? wireBytes = null,
        long? wireItems = null,
        long? payloadBytesPerItem = null) =>
        new()
        {
            FullName = $"OpenCode.Sdk.Performance.Tests.Benchmarks.HealthBenchmarks.{method}(Fixture: health)",
            Family = "Health",
            Method = method,
            Parameters = "Fixture=health",
            Runtime = runtime,
            AllocatedBytes = allocatedBytes,
            MedianNanoseconds = medianNanoseconds,
            WireBytes = wireBytes,
            WireItems = wireItems,
            PayloadBytesPerItem = payloadBytesPerItem,
        };
}
