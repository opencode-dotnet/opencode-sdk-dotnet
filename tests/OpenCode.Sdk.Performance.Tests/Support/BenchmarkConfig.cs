using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
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
/// <remarks>
/// BenchmarkDotNet 0.15.8 does not know the <c>net11.0</c> moniker, so <c>--runtimes net11.0</c> is
/// refused. <see cref="Net11Switch"/> adds a .NET 11 leg the way its documentation describes for a
/// runtime it does not know yet: a job on <c>CoreRuntime.CreateForNewVersion</c>, labelled
/// <c>.NET 11.0</c> in the exports like the legs <c>--runtimes</c> names.
/// </remarks>
internal static class BenchmarkConfig
{
    /// <summary>Wide enough that fixture names print whole instead of being elided in the middle.</summary>
    private const int MaxParameterColumnWidth = 40;

    /// <summary>The argument that adds the .NET 11 leg; it is taken off before BenchmarkDotNet reads the rest.</summary>
    public const string Net11Switch = "--net11";

    public static IConfig Create(bool net11)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .WithSummaryStyle(SummaryStyle.Default.WithSizeUnit(SizeUnit.B).WithMaxParameterColumnWidth(MaxParameterColumnWidth))
            .AddExporter(JsonExporter.Full)
            .AddDiagnoser(new WireFixtureDiagnoser())
            .AddColumn(
                new AllocatedPerItemColumn(),
                new AllocationAmplificationColumn());
        return net11
            ? config.AddJob(Job.Default.WithRuntime(CoreRuntime.CreateForNewVersion("net11.0", ".NET 11.0")))
            : config;
    }
}
