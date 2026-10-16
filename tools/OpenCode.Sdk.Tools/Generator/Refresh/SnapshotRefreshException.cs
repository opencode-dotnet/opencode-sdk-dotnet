namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>A refused synchronizer step; the message states the exact wall that answered.</summary>
public sealed class SnapshotRefreshException : ToolRefusalException
{
    /// <summary>Creates the refusal with its wall message.</summary>
    public SnapshotRefreshException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal with its wall message and cause.</summary>
    public SnapshotRefreshException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an empty refusal; required by the exception design rules, never used directly.</summary>
    public SnapshotRefreshException()
    {
    }
}
