using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Macro;

/// <summary>Reads process memory at full-collection checkpoints and keeps the highest retained reading.</summary>
internal sealed class RetainedMemory : IDisposable
{
    private const string MeasurementReason =
        "A memory measurement harness reads retained bytes after a full collection, as BenchmarkDotNet's memory diagnoser does.";

    private readonly Process _process = Process.GetCurrentProcess();

    public long PeakRetainedBytes { get; private set; }

    [SuppressMessage("Performance", "S1215", Justification = MeasurementReason)]
    [SlopwatchSuppress("SW002", "A memory measurement harness reads retained bytes after a full collection, as BenchmarkDotNet's memory diagnoser does.")]
    public long ReadRetainedBytes()
    {
        var retained = GC.GetTotalMemory(forceFullCollection: true);
        PeakRetainedBytes = Math.Max(PeakRetainedBytes, retained);
        return retained;
    }

    public long ReadPrivateBytes()
    {
        _process.Refresh();
        return _process.PrivateMemorySize64;
    }

    public void Dispose() => _process.Dispose();
}
