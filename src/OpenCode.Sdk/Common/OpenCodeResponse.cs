using System.Globalization;
using System.Text;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>Carries the shared metadata of every typed opencode response envelope.</summary>
public abstract record OpenCodeResponse
{
    /// <summary>Gets the HTTP status code of the response.</summary>
    public required int Status { get; init; }

    /// <summary>Gets a value indicating whether the response is an API error.</summary>
    public bool IsError { get; init; }

    /// <summary>Gets the typed API error, or <see langword="null"/> when the error body could not be parsed.</summary>
    public IOpenCodeError? Error { get; init; }

    /// <summary>
    /// Gets the raw error body; populated only on the error path. The printed form of a response
    /// leaves it out, because a server can echo request data into an error body.
    /// </summary>
    public string? RawBody { get; init; }

    /// <summary>
    /// Prints the status, the error flag, and the typed error, in the record's own shape. The raw
    /// error body is left out: a logged response must not carry whatever the server echoed, and a
    /// caller who needs it reads <see cref="RawBody"/>.
    /// </summary>
    /// <param name="builder">The builder the record's <c>ToString</c> fills.</param>
    /// <returns><see langword="true"/>, because a member was printed.</returns>
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder
            .Append("Status = ").Append(Status.ToString(CultureInfo.InvariantCulture))
            .Append(", IsError = ").Append(IsError)
            .Append(", Error = ").Append(Error);
        return true;
    }
}
