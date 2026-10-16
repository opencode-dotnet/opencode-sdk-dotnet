namespace OpenCode.Sdk.Tools.Benchmarks;

/// <summary>A benchmark input that cannot be compared.</summary>
public sealed class BenchmarkRunException : ToolRefusalException
{
    /// <summary>Supports a refusal without a custom diagnostic.</summary>
    public BenchmarkRunException() { }
    /// <summary>Preserves the caller's diagnostic.</summary>
    public BenchmarkRunException(string message) : base(message) { }
    /// <summary>Preserves the diagnostic and its underlying cause.</summary>
    public BenchmarkRunException(string message, Exception innerException) : base(message, innerException) { }
}
