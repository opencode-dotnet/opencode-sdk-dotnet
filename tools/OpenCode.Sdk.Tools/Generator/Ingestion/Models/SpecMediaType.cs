using System.Net.Http.Headers;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes a normalized response or request media type.</summary>
internal sealed record SpecMediaType
{
    /// <summary>Gets the media type exactly as declared by the document.</summary>
    public required string Raw
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the parameter-free, invariant-lowercase media type.</summary>
    public required string Stripped
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets a value indicating whether the media type carries JSON.</summary>
    public required bool IsJson { get; init; }

    /// <summary>Gets a value indicating whether the media type is an event stream.</summary>
    public required bool IsEventStream { get; init; }

    /// <summary>Creates a normalized media type from a wire declaration.</summary>
    /// <param name="raw">The media type declaration.</param>
    /// <returns>The normalized media type.</returns>
    public static SpecMediaType Create(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        if (!MediaTypeHeaderValue.TryParse(raw, out var parsed) || string.IsNullOrEmpty(parsed.MediaType))
        {
            throw new ArgumentException($"Media type '{raw}' is malformed.", nameof(raw));
        }

        var stripped = parsed.MediaType.ToLowerInvariant();
        var separator = stripped.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == stripped.Length - 1)
        {
            throw new ArgumentException($"Media type '{raw}' is malformed.", nameof(raw));
        }

        return new SpecMediaType
        {
            Raw = raw,
            Stripped = stripped,
            IsJson = string.Equals(stripped, "application/json", StringComparison.Ordinal)
                     || stripped.EndsWith("+json", StringComparison.Ordinal),
            IsEventStream = string.Equals(stripped, "text/event-stream", StringComparison.Ordinal),
        };
    }
}
