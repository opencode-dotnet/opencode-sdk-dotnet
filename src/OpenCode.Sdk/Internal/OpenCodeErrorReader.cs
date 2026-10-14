using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads a typed error body tolerantly for every response plane. Malformed JSON yields
/// <see langword="null"/> so the raw body remains the only record; an unknown tag keeps its
/// carrier; a known tag outside the operation's status map — or on an undeclared status,
/// when the allowed tags are <see langword="null"/> — downgrades to the unknown carrier so
/// the operation contract never widens.
/// </summary>
internal static class OpenCodeErrorReader
{
    private const int UnauthorizedStatus = 401;

    /// <summary>
    /// The sentence a bare 401 cannot supply itself. It names the option rather than reading the
    /// environment: the SDK never reads an environment variable, so resolving
    /// <c>OPENCODE_PASSWORD</c> stays the caller's own step.
    /// </summary>
    private const string MissingCredentialHint =
        " The client sent no credential: the opencode CLI always starts its server with a password, " +
        "so pass the one it printed as 'server password <pw>', or the one you set through " +
        "OPENCODE_PASSWORD, in OpenCodeClientOptions.Password.";

    public static IOpenCodeError? Read(string rawBody, string[]? allowedTags)
    {
        ArgumentNullException.ThrowIfNull(rawBody);

        try
        {
            var error = JsonSerializer.Deserialize(rawBody, OpenCodeJsonContext.Default.IOpenCodeError);
            return error switch
            {
                null => null,
                UnknownOpenCodeError unknown => unknown,
                // The tag sets are the generated per-status arrays, so the scan stays an
                // allocation-free ordinal lookup on this per-error hot path.
                _ when allowedTags is not null && Array.IndexOf(allowedTags, error.Tag) >= 0 => error,
                _ => Downgrade(error.Tag, rawBody),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the failure an error status raises on the throwing channel. A 401 reached by a client
    /// that was built without a password carries one extra sentence: an <c>opencode serve</c>
    /// process always runs with a password and short-circuits an uncredentialed request with an
    /// empty 401 body, so the wire itself gives that caller nothing to diagnose with.
    /// </summary>
    /// <param name="status">The HTTP status the server answered with.</param>
    /// <param name="error">The typed error the body carried, when there was one.</param>
    /// <param name="rawBody">The exact response body, retained on every failure.</param>
    /// <param name="credentialSent">
    /// Whether the request carried a Basic credential. A wrong password is not a missing one, so the
    /// message is unchanged whenever the client had a credential to send.
    /// </param>
    public static OpenCodeApiException CreateApiException(int status, IOpenCodeError? error, string? rawBody,
        bool credentialSent)
    {
        var statusText = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var message = error is null
            ? $"The opencode API returned status {statusText}."
            : $"The opencode API returned status {statusText} ('{error.Tag}').";

        if (status is UnauthorizedStatus && !credentialSent)
        {
            message += MissingCredentialHint;
        }

        return new OpenCodeApiException(message, status, error, rawBody);
    }

    private static UnknownOpenCodeError Downgrade(string tag, string rawBody)
    {
        using var document = JsonDocument.Parse(rawBody);
        return new UnknownOpenCodeError(tag, document.RootElement);
    }
}
