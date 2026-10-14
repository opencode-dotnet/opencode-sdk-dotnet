namespace OpenCode.Sdk;

/// <summary>Represents a network or protocol failure below the opencode API contract.</summary>
public class OpenCodeTransportException : OpenCodeException
{
    /// <summary>Initializes a new instance of the <see cref="OpenCodeTransportException"/> class.</summary>
    public OpenCodeTransportException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeTransportException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    public OpenCodeTransportException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeTransportException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OpenCodeTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
