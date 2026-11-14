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
    /// The hint distinguishes missing from wrong credentials; a 401 cannot. It names the option, not the
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
    /// Builds the throwing-channel failure. Its status sentence includes the server's message,
    /// selected as upstream's <c>client.ts</c> <c>declared(body)</c> does. A passwordless client's 401
    /// adds a hint because a 401 cannot distinguish a missing credential from a wrong one.
    /// </summary>
    /// <param name="status">The HTTP status the server answered with.</param>
    /// <param name="error">The typed error the body carried, when there was one.</param>
    /// <param name="rawBody">The exact response body, retained on every failure.</param>
    /// <param name="credentialSent">
    /// Whether the request carried a Basic credential. The hint is withheld when a credential was
    /// sent; the status sentence still includes the server's message when present.
    /// </param>
    public static OpenCodeApiException CreateApiException(int status, IOpenCodeError? error, string? rawBody,
        bool credentialSent)
    {
        var statusText = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var message = error is null
            ? $"The opencode API returned status {statusText}."
            : $"The opencode API returned status {statusText} ('{error.Tag}').";

        if (error is not null)
        {
            // Read only returns a non-null error after parsing the body as JSON.
            using var document = JsonDocument.Parse(rawBody!);
            var root = document.RootElement;
            string? selected = null;
            if (root.TryGetProperty("message", out var top) && top.ValueKind is JsonValueKind.String)
            {
                selected = top.GetString();
            }
            else if (root.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Object
                     && data.TryGetProperty("message", out var nested) && nested.ValueKind is JsonValueKind.String)
            {
                selected = nested.GetString();
            }

            if (selected is { Length: > 0 })
            {
                message = $"The opencode API returned status {statusText} ('{error.Tag}'): {selected}.";
            }
        }

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
