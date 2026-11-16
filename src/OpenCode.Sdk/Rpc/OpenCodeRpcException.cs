using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>A plugin RPC call's failure, unwrapped as opencode's own client unwraps it.</summary>
/// <remarks>
/// ErrorType is an RPC system type such as rpc.unavailable or rpc.invalid_input, or the plugin's declared
/// error type. The exception message is the failure's message, and ErrorData is the failure's data;
/// Status, Error and RawBody retain the HTTP failure.
/// </remarks>
public sealed class OpenCodeRpcException : OpenCodeApiException
{
    /// <summary>Initializes an exception without an RPC failure type or data.</summary>
    public OpenCodeRpcException()
    {
        ErrorType = string.Empty;
    }

    /// <summary>Initializes an exception with a message and no RPC failure type or data.</summary>
    /// <param name="message">The failure description.</param>
    public OpenCodeRpcException(string message)
        : base(message)
    {
        ErrorType = string.Empty;
    }

    /// <summary>Initializes an exception with a message, underlying failure, and no RPC failure type or data.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public OpenCodeRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorType = string.Empty;
    }

    /// <summary>Initializes an exception with the unwrapped RPC failure and its HTTP response facts.</summary>
    /// <param name="type">The failure type.</param>
    /// <param name="message">The failure's own message.</param>
    /// <param name="data">The failure's data, when present.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="error">The typed API error.</param>
    /// <param name="rawBody">The raw error body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public OpenCodeRpcException(string type, string message, JsonElement? data, int status, IOpenCodeError? error, string? rawBody)
        : base(message, status, error, rawBody)
    {
        ArgumentNullException.ThrowIfNull(type);
        ErrorType = type;
        ErrorData = data;
    }

    /// <summary>Gets the failure type; empty when built through a standard exception constructor.</summary>
    public string ErrorType { get; }

    /// <summary>Gets the failure's data, or null when the failure carries none.</summary>
    public JsonElement? ErrorData { get; }
}
