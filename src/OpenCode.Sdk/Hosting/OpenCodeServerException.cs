namespace OpenCode.Sdk;

/// <summary>
/// A local server door failed: a standalone server could not start, report readiness, or stop,
/// or background-service discovery or ensure could not resolve the user home its registration
/// roots hang off, timed out, or refused a version mismatch. An absent or unusable registration
/// is not a failure for discovery, which reports it as null; ensure starts a service instead.
/// </summary>
public class OpenCodeServerException : OpenCodeException
{
    /// <summary>Initializes a new instance of the <see cref="OpenCodeServerException"/> class.</summary>
    public OpenCodeServerException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeServerException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    public OpenCodeServerException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeServerException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OpenCodeServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
