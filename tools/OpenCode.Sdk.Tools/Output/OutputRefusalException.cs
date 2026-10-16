namespace OpenCode.Sdk.Tools.Output;

/// <summary>An output placement or formatter prerequisite the caller must repair.</summary>
public sealed class OutputRefusalException : ToolRefusalException
{
    /// <summary>Supports a refusal without a custom diagnostic.</summary>
    public OutputRefusalException() { }
    /// <summary>Preserves the caller's diagnostic.</summary>
    public OutputRefusalException(string message) : base(message) { }
    /// <summary>Preserves the diagnostic and its underlying cause.</summary>
    public OutputRefusalException(string message, Exception innerException) : base(message, innerException) { }
}
