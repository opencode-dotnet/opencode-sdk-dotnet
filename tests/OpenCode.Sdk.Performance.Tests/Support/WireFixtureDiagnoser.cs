using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// Emits the case's <see cref="WireFixture"/> figures — wire bytes, item count, and payload bytes
/// per item — as BenchmarkDotNet metrics. Metrics render as summary columns and land in every
/// <c>*-report-full.json</c> export, so the exact quantities the table prints also survive into
/// archived runs and the <c>compare-benchmarks</c> join, never re-derived from display strings.
/// A case without a fixture (a pure-composition rung) is excluded via <see cref="RunMode.None"/>
/// and carries no wire metrics anywhere. The diagnoser piggybacks on the main run
/// (<see cref="RunMode.NoOverhead"/>): every value rides the fixture parameter, not a measurement.
/// </summary>
internal sealed class WireFixtureDiagnoser : IDiagnoser
{
    public IEnumerable<string> Ids => [nameof(WireFixtureDiagnoser)];

    public IEnumerable<IExporter> Exporters => [];

    public IEnumerable<IAnalyser> Analysers => [];

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) =>
        WireFixtureParameter.Find(benchmarkCase) is null ? RunMode.None : RunMode.NoOverhead;

    public ValueTask HandleAsync(HostSignal signal, DiagnoserActionParameters parameters, CancellationToken cancellationToken)
    {
        // Nothing to observe during the run: the metric values ride the fixture parameter.
        return default;
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (WireFixtureParameter.Find(results.BenchmarkCase) is not { } fixture)
        {
            return [];
        }

        return
        [
            new Metric(WireMetricDescriptor.WireBytes, fixture.WireBytes),
            new Metric(WireMetricDescriptor.WireItems, fixture.Items),
            new Metric(WireMetricDescriptor.PayloadBytesPerItem, fixture.PayloadBytesPerItem),
        ];
    }

    public void DisplayResults(ILogger logger)
    {
        // The metrics render through the summary's metric columns; there is no extra prose.
    }

    public async IAsyncEnumerable<ValidationError> ValidateAsync(ValidationParameters validationParameters)
    {
        yield break;
    }

    /// <summary>
    /// Identity of one wire metric: the <c>Id</c> keys the JSON export (and the comparison tool's
    /// reader), the display members carry the wording of the former plain wire columns.
    /// </summary>
    private sealed class WireMetricDescriptor : IMetricDescriptor
    {
        /// <summary>BenchmarkDotNet's own memory metrics occupy priorities 0–3 (Gen0..2, Allocated).</summary>
        private const int FirstWirePriority = 4;

        internal static readonly IMetricDescriptor WireBytes = new WireMetricDescriptor(
            id: "WireBytes",
            displayName: "Wire B",
            legend: "Exact wire body bytes one operation consumes (envelope/framing included)",
            unitType: UnitType.Size,
            unit: "B",
            priorityInCategory: FirstWirePriority);

        internal static readonly IMetricDescriptor WireItems = new WireMetricDescriptor(
            id: "WireItems",
            displayName: "Items",
            legend: "Payloads or frames consumed per operation",
            unitType: UnitType.Dimensionless,
            unit: "Count",
            priorityInCategory: FirstWirePriority + 1);

        internal static readonly IMetricDescriptor PayloadBytesPerItem = new WireMetricDescriptor(
            id: "PayloadBytesPerItem",
            displayName: "Payload B/item",
            legend: "JSON payload bytes per item, excluding envelope and SSE framing (average for a mixed body)",
            unitType: UnitType.Size,
            unit: "B",
            priorityInCategory: FirstWirePriority + 2);

        private WireMetricDescriptor(string id, string displayName, string legend, UnitType unitType, string unit, int priorityInCategory)
        {
            Id = id;
            DisplayName = displayName;
            Legend = legend;
            UnitType = unitType;
            Unit = unit;
            PriorityInCategory = priorityInCategory;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Legend { get; }

        public string NumberFormat => "N0";

        public UnitType UnitType { get; }

        public string Unit { get; }

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory { get; }

        public bool GetIsAvailable(Metric metric) => true;
    }
}
