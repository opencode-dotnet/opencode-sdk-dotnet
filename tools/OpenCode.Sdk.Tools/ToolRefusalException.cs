namespace OpenCode.Sdk.Tools;

/// <summary>A tool refusal caused by input or repository state the caller can repair.</summary>
public abstract class ToolRefusalException : Exception
{
    /// <summary>Supports a refusal without a custom diagnostic.</summary>
    protected ToolRefusalException() { }
    /// <summary>Preserves the caller's diagnostic.</summary>
    protected ToolRefusalException(string message) : base(message) { }
    /// <summary>Preserves the diagnostic and its underlying cause.</summary>
    protected ToolRefusalException(string message, Exception innerException) : base(message, innerException) { }
}
