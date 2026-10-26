namespace OpenCode.Sdk.Tools.Benchmarks.Models;

/// <summary>MemoryDiagnoser output of an exported benchmark case.</summary>
internal sealed record BenchmarkReportMemory
{
    public required long? BytesAllocatedPerOperation { get; init; }
}
