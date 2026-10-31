using System.ComponentModel;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// Reports the whole-process peak working set read after the measured iterations, including the
/// class's setup, BenchmarkDotNet's engine and the warm-up, not the stage's own cost.
/// </summary>
internal sealed class GeneratorPeakMemoryDiagnoser : IDiagnoser
{
    private readonly Dictionary<BenchmarkCase, long?> _peaks = [];

    public IEnumerable<string> Ids => [nameof(GeneratorPeakMemoryDiagnoser)];

    public IEnumerable<IExporter> Exporters => [];

    public IEnumerable<IAnalyser> Analysers => [];

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) =>
        GeneratorPeakMemoryPolicy.ShouldMeasure(benchmarkCase.Descriptor.Type.Namespace) ? RunMode.NoOverhead : RunMode.None;

    public ValueTask HandleAsync(HostSignal signal, DiagnoserActionParameters parameters, CancellationToken cancellationToken)
    {
        if (signal == HostSignal.AfterActualRun && GeneratorPeakMemoryPolicy.ShouldMeasure(parameters.BenchmarkCase.Descriptor.Type.Namespace))
        {
            long? peak;
            try
            {
                parameters.Process?.Refresh();
                peak = parameters.Process?.PeakWorkingSet64;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                peak = null;
                parameters.Config.GetCompositeLogger().WriteLineError($"{parameters.BenchmarkCase.DisplayInfo}: process peak working set unreadable: {exception.GetType().Name}: {exception.Message}");
            }

            _peaks[parameters.BenchmarkCase] = GeneratorPeakMemoryPolicy.MetricValue(peak);
        }

        return default;
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results) =>
        _peaks.TryGetValue(results.BenchmarkCase, out var peak) && peak is { } value
            ? [new Metric(new PeakWorkingSetDescriptor(), value)] : [];

    public void DisplayResults(ILogger logger)
    {
        // The metric column and exports carry the result.
    }

    public async IAsyncEnumerable<ValidationError> ValidateAsync(ValidationParameters validationParameters)
    {
        yield break;
    }

    private sealed class PeakWorkingSetDescriptor : IMetricDescriptor
    {
        public string Id => "GeneratorPeakWorkingSet";

        public string DisplayName => "Process peak working set";

        public string Legend => "Whole-process peak working set read after the measured iterations, including the class's setup, BenchmarkDotNet's engine and the warm-up, not the stage's own cost";

        public string NumberFormat => "N0";

        public UnitType UnitType => UnitType.Size;

        public string Unit => "B";

        public bool TheGreaterTheBetter => false;

        public int PriorityInCategory => 4;

        public bool GetIsAvailable(Metric metric) => true;
    }
}
