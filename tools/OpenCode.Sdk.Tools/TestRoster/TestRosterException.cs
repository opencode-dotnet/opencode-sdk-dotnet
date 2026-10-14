namespace OpenCode.Sdk.Tools.TestRoster;

/// <summary>A refused roster step; the message states the exact file or entry that refused.</summary>
public sealed class TestRosterException : Exception
{
    /// <summary>Creates the refusal with its message.</summary>
    public TestRosterException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal with its message and cause.</summary>
    public TestRosterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an empty refusal; required by the exception design rules, never used directly.</summary>
    public TestRosterException()
    {
    }
}
