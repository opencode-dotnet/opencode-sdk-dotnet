using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>Represents a non-success response returned by the opencode API.</summary>
public class OpenCodeApiException : OpenCodeException
{
    /// <summary>Initializes a new instance of the <see cref="OpenCodeApiException"/> class.</summary>
    public OpenCodeApiException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeApiException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    public OpenCodeApiException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeApiException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OpenCodeApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCodeApiException"/> class.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="status">The HTTP status code of the error response.</param>
    /// <param name="error">The typed API error, when the error body carried a known shape.</param>
    /// <param name="rawBody">The raw error body.</param>
    public OpenCodeApiException(string message, int status, IOpenCodeError? error, string? rawBody)
        : base(message)
    {
        Status = status;
        Error = error;
        RawBody = rawBody;
    }

    /// <summary>Gets the HTTP status code of the error response.</summary>
    public int Status { get; }

    /// <summary>Gets the typed API error, or <see langword="null"/> when the error body could not be parsed.</summary>
    public IOpenCodeError? Error { get; }

    /// <summary>Gets the raw error body.</summary>
    public string? RawBody { get; }
}
