using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using OpenCode.Sdk.Performance.Tests.Support.Columns;
using Perfolizer.Metrology;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// The suite-wide configuration: exact bytes instead of rounded KB, the wire-fixture figures as
/// metrics (rendered beside the allocation column and exported into the full JSON), the derived
/// allocation-per-item and amplification columns, and the full JSON export so raw per-operation
/// bytes and measurements survive every run without re-deriving them from prose.
/// </summary>
internal static class BenchmarkConfig
{
    /// <summary>Wide enough that fixture names print whole instead of being elided in the middle.</summary>
    private const int MaxParameterColumnWidth = 40;

    /// <summary>How long one runtime's build may take.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    public static IConfig Create() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .WithSummaryStyle(SummaryStyle.Default.WithSizeUnit(SizeUnit.B).WithMaxParameterColumnWidth(MaxParameterColumnWidth))
            .AddExporter(JsonExporter.Full)
            .AddJob(Job.Default.DontEnforcePowerPlan().AsMutator())
            .AddDiagnoser(new WireFixtureDiagnoser())
            .AddDiagnoser(new GeneratorPeakMemoryDiagnoser())
            .AddColumn(
                new AllocatedPerItemColumn(),
                new AllocationAmplificationColumn())
            // A runtime's build compiles the suite and the SDK for a target framework the solution
            // build may not have built, which on a loaded host outlasts the default two minutes.
            .WithBuildTimeout(BuildTimeout);
}
