namespace OpenCode.Sdk.Tools.Clones;

/// <summary>A refusal of the clone gate: a prerequisite is missing, or the detector produced no report to judge.</summary>
public sealed class CloneDetectorException : ToolRefusalException
{
    /// <summary>Creates the refusal with its message.</summary>
    public CloneDetectorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal with its message and cause.</summary>
    public CloneDetectorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an empty refusal; required by the exception design rules, never used directly.</summary>
    public CloneDetectorException()
    {
    }
}
