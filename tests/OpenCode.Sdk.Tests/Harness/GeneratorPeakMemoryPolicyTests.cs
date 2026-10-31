namespace OpenCode.Sdk.Tests.Harness;

public sealed class GeneratorPeakMemoryPolicyTests
{
    [Test]
    [Arguments("OpenCode.Sdk.Performance.Tests.Generator", true)]
    [Arguments("OpenCode.Sdk.Performance.Tests.Benchmarks", false)]
    public async Task ShouldMeasure_Should_Select_Only_The_Generator_Namespace(string benchmarkNamespace, bool expected)
    {
        var measured = GeneratorPeakMemoryPolicy.ShouldMeasure(benchmarkNamespace);

        await Assert.That(measured).IsEqualTo(expected);
    }

    [Test]
    public async Task MetricValue_Should_Preserve_A_Positive_Value()
    {
        var metric = GeneratorPeakMemoryPolicy.MetricValue(123L);

        await Assert.That(metric).IsEqualTo(123L);
    }

    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    [Arguments(null)]
    public async Task MetricValue_Should_Omit_An_Unreadable_Or_Non_Positive_Value(long? peakBytes)
    {
        var metric = GeneratorPeakMemoryPolicy.MetricValue(peakBytes);

        await Assert.That(metric).IsNull();
    }
}
