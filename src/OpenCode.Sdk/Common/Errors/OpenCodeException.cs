namespace OpenCode.Sdk;

/// <summary>Represents any failure raised by the opencode SDK.</summary>
public class OpenCodeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="OpenCodeException"/> class.</summary>
    public OpenCodeException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    public OpenCodeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OpenCodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
