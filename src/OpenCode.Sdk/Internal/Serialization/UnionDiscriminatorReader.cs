using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>Reads top-level union markers without materializing the known payload as a JSON DOM.</summary>
internal sealed class UnionDiscriminatorReader
{
    /// <summary>Bounds the stack copy that unescapes a marker; a longer raw marker takes the string path.</summary>
    private const int MaxStackMarkerLength = 128;

    private readonly JsonTokenType _objectToken = JsonTokenType.StartObject;

    /// <summary>
    /// Dispatches a string marker against the tag table: a known tag returns its mapped value
    /// from the marker's UTF-8 bytes, decoding only a marker whose raw form exceeds the stack copy,
    /// while an unknown tag materializes the decoded marker for the caller's carrier.
    /// </summary>
    public bool TryFindKnown<TValue>(
        ref Utf8JsonReader reader,
        string propertyName,
        string conceptName,
        UnionTagTable<TValue> typesByTag,
        [MaybeNullWhen(false)] out TValue type,
        [NotNullWhen(false)] out string? marker)
    {
        ArgumentNullException.ThrowIfNull(typesByTag);

        var markerReader = Find(ref reader, propertyName, conceptName);
        return Dispatch(ref markerReader, propertyName, typesByTag, out type, out marker);
    }

    public bool ReadBoolean(ref Utf8JsonReader reader, string propertyName, string conceptName)
    {
        var marker = Find(ref reader, propertyName, conceptName);
        if (marker.TokenType is not (JsonTokenType.True or JsonTokenType.False))
        {
            throw new JsonException($"The '{propertyName}' marker must be a boolean.");
        }

        return marker.GetBoolean();
    }

    public void RequireString(ref Utf8JsonReader reader, string propertyName, string expected, string conceptName)
    {
        var marker = Find(ref reader, propertyName, conceptName);
        if (marker.TokenType is not JsonTokenType.String || !marker.ValueTextEquals(expected))
        {
            throw new JsonException($"The '{propertyName}' marker must be '{expected}'.");
        }
    }

    public void RequireBoolean(ref Utf8JsonReader reader, string propertyName, bool expected, string conceptName)
    {
        var marker = Find(ref reader, propertyName, conceptName);
        if (marker.TokenType != (expected ? JsonTokenType.True : JsonTokenType.False))
        {
            throw new JsonException($"The '{propertyName}' marker must be '{expected}'.");
        }
    }

    private static bool Dispatch<TValue>(
        ref Utf8JsonReader markerReader,
        string propertyName,
        UnionTagTable<TValue> typesByTag,
        [MaybeNullWhen(false)] out TValue type,
        [NotNullWhen(false)] out string? marker)
    {
        if (markerReader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException($"The '{propertyName}' marker must be a string.");
        }

        if (!markerReader.HasValueSequence && !markerReader.ValueIsEscaped)
        {
            if (typesByTag.TryGetValue(markerReader.ValueSpan, out type))
            {
                marker = null;
                return true;
            }

            return Unknown(ref markerReader, propertyName, null, out type, out marker);
        }

        // The raw length counts escapes, so it bounds the unescaped copy.
        var rawLength = markerReader.HasValueSequence ? markerReader.ValueSequence.Length : markerReader.ValueSpan.Length;
        if (rawLength <= MaxStackMarkerLength)
        {
            Span<byte> buffer = stackalloc byte[MaxStackMarkerLength];
            if (typesByTag.TryGetValue(buffer[..markerReader.CopyString(buffer)], out type))
            {
                marker = null;
                return true;
            }

            return Unknown(ref markerReader, propertyName, null, out type, out marker);
        }

        var decoded = markerReader.GetString();
        if (decoded is not null && typesByTag.TryGetValue(decoded, out type))
        {
            marker = null;
            return true;
        }

        return Unknown(ref markerReader, propertyName, decoded, out type, out marker);
    }

    /// <summary>
    /// Hands the decoded marker to the unknown carrier; no declared tag is whitespace, so refusing
    /// a blank marker only after the lookup missed refuses exactly what a check before it would.
    /// </summary>
    private static bool Unknown<TValue>(
        ref Utf8JsonReader markerReader,
        string propertyName,
        string? decoded,
        [MaybeNullWhen(false)] out TValue type,
        [NotNullWhen(false)] out string? marker)
    {
        if ((decoded ?? markerReader.GetString()) is not { } value || string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"The '{propertyName}' marker must be a non-empty string.");
        }

        marker = value;
        type = default;
        return false;
    }

    /// <summary>
    /// Scans a copy of the reader for the last top-level occurrence of the marker property and
    /// returns a reader positioned on its value. A payload that is not a well-formed object, or
    /// that does not carry the property, is malformed under the union's contract.
    /// </summary>
    private Utf8JsonReader Find(ref Utf8JsonReader reader, string propertyName, string conceptName)
    {
        if (reader.TokenType != _objectToken)
        {
            throw new JsonException($"The {conceptName} payload must be a JSON object.");
        }

        var scan = reader;
        var found = false;
        var marker = default(Utf8JsonReader);
        while (scan.Read() && scan.TokenType is not JsonTokenType.EndObject)
        {
            if (scan.TokenType is not JsonTokenType.PropertyName)
            {
                throw new JsonException($"The {conceptName} payload must be a JSON object.");
            }

            var matches = scan.ValueTextEquals(propertyName);
            if (!scan.Read())
            {
                throw new JsonException($"The {conceptName} payload ended before '{propertyName}'.");
            }

            if (matches)
            {
                marker = scan;
                found = true;
            }

            if (!scan.TrySkip())
            {
                throw new JsonException($"The {conceptName} payload ended before '{propertyName}'.");
            }
        }

        return found
            ? marker
            : throw new JsonException($"The {conceptName} payload must contain '{propertyName}'.");
    }
}
