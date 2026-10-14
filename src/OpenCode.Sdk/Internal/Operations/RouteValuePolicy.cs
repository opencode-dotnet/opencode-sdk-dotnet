using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>Escapes URI components identically across modern and downlevel target frameworks.</summary>
internal static class RouteValuePolicy
{
    private const int MaximumInputLength = 32_766;

    /// <summary>Refuses legacy-incompatible inputs before delegating to the platform escaper.</summary>
    public static string Escape(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        Debug.Assert(!string.IsNullOrWhiteSpace(parameterName));

        if (value.Length > MaximumInputLength)
        {
            throw new ArgumentException($"Route values must not exceed {MaximumInputLength} UTF-16 code units.", parameterName);
        }

        if (!HasValidUtf16(value))
        {
            throw new ArgumentException("Route values must contain valid UTF-16.", parameterName);
        }

        return Uri.EscapeDataString(value);
    }

    /// <summary>
    /// Escapes a value that becomes one path segment. Escaping leaves a dot segment intact and
    /// <see cref="Uri"/> then resolves it away, silently addressing a different route than the
    /// caller named, so the refusal belongs here rather than at every call site that composes a
    /// path. A query value takes <see cref="Escape"/> instead: a dot is an ordinary value there.
    /// </summary>
    public static string EscapeSegment(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value is "." or "..")
        {
            throw new ArgumentException("Route values must not be dot segments.", parameterName);
        }

        return Escape(value, parameterName);
    }

    /// <summary>
    /// Escapes a value that fills a route's trailing wildcard, the way upstream's generated client
    /// does (<c>encodePath</c>: split on <c>/</c>, <c>encodeURIComponent</c> each segment, join
    /// with <c>/</c>), so a nested path keeps its separators and every other character is escaped
    /// exactly as the first-party client escapes it. A dot segment is refused as
    /// <see cref="EscapeSegment"/> refuses it: the URL would resolve it away and address a
    /// different path than the caller named, where upstream's client resolves it silently.
    /// </summary>
    public static string EscapeTail(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        Debug.Assert(!string.IsNullOrWhiteSpace(parameterName));

        if (value.Length > MaximumInputLength)
        {
            throw new ArgumentException($"Route values must not exceed {MaximumInputLength} UTF-16 code units.", parameterName);
        }

        if (!HasValidUtf16(value))
        {
            throw new ArgumentException("Route values must contain valid UTF-16.", parameterName);
        }

        var segments = value.Split('/');
        if (Array.Exists(segments, static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Route paths must not contain dot segments.", parameterName);
        }

        return string.Join('/', Array.ConvertAll(segments, EncodeUriComponent));
    }

    /// <summary>Escapes an SDK-owned wire name whose representability is an internal invariant.</summary>
    public static string EscapeName(string value)
    {
        Debug.Assert(value.Length <= MaximumInputLength);
        Debug.Assert(HasValidUtf16(value));
        return Uri.EscapeDataString(value);
    }

    /// <summary>
    /// ECMAScript's <c>encodeURIComponent</c>: every UTF-8 byte is percent-encoded with upper-case
    /// hex except the letters, the digits and <c>-_.!~*'()</c>. <c>Uri.EscapeDataString</c>
    /// escapes <c>!*'()</c> as well, which the server decodes to the same path but which would
    /// not be the request upstream's client sends.
    /// </summary>
    private static string EncodeUriComponent(string segment)
    {
        var bytes = Encoding.UTF8.GetBytes(segment);
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var value in bytes)
        {
            if (IsUnreserved(value))
            {
                _ = builder.Append((char)value);
                continue;
            }

            _ = builder.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(byte value) => value is
        (>= (byte)'a' and <= (byte)'z')
        or (>= (byte)'A' and <= (byte)'Z')
        or (>= (byte)'0' and <= (byte)'9')
        or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'!' or (byte)'~' or (byte)'*' or (byte)'\'' or (byte)'(' or (byte)')';

    private static bool HasValidUtf16(string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index];
            if (!char.IsSurrogate(character))
            {
                index++;
                continue;
            }

            if (!char.IsHighSurrogate(character) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }
}
