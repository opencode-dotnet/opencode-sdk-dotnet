using System.Text;
using System.Text.Unicode;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Matches HttpContent string decoding while retaining valid UTF-8 for direct JSON
/// materialization: charset wins over BOM sniffing, a preamble the selected encoding owns is
/// stripped, and malformed input replacement-decodes instead of throwing. Knowledge source:
/// BCL-derived — the parity contract is proven by the differential matrix against real
/// <see cref="HttpContent"/> decoding on every target.
/// </summary>
internal static class ResponseEncodingPolicy
{
    private const int BigEndianUnicodeCodePage = 1201;
    private const int UnicodeCodePage = 1200;
    private const int Utf8CodePage = 65001;
    private const int Utf32CodePage = 12000;

    private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];

    /// <summary>Checked before the UTF-16 little-endian preamble, whose two bytes it starts with.</summary>
    private static ReadOnlySpan<byte> Utf32LittleEndianPreamble => [0xFF, 0xFE, 0x00, 0x00];

    private static ReadOnlySpan<byte> Utf16LittleEndianPreamble => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BigEndianPreamble => [0xFE, 0xFF];

    public static EncodedResponseBody Decode(byte[] body, string? charset)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Decode(body, body.Length, charset);
    }

    /// <summary>Decodes the first <paramref name="count"/> bytes; a pooled backing array may be longer.</summary>
    public static EncodedResponseBody Decode(byte[] body, int count, string? charset)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, body.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count is 0)
        {
            // HttpContent returns an empty string before consulting an invalid charset.
            return new EncodedResponseBody(ReadOnlyMemory<byte>.Empty, DecodedBody: null);
        }

        var encoding = ResolveEncoding(body, count, charset, out var preambleLength);
        if (encoding.CodePage == Utf8CodePage && Utf8.IsValid(body.AsSpan(preambleLength, count - preambleLength)))
        {
            return new EncodedResponseBody(body.AsMemory(preambleLength, count - preambleLength), DecodedBody: null);
        }

        // ReadAsStringAsync replacement-decodes malformed input, so the fallback decodes
        // permissively rather than throwing. Downlevel stays on the (array, index, count)
        // overload deliberately: the span Encoding shims allocate on those targets.
        return new EncodedResponseBody(
            Utf8Body: default,
            encoding.GetString(body, preambleLength, count - preambleLength));
    }

    private static Encoding ResolveEncoding(byte[] body, int count, string? charset, out int preambleLength)
    {
        if (charset is not null)
        {
            var declared = ResolveDeclaredEncoding(charset);
            preambleLength = GetPreambleLength(body, count, declared);
            return declared;
        }

        var span = body.AsSpan(0, count);
        if (span.StartsWith(Utf8Preamble))
        {
            preambleLength = Utf8Preamble.Length;
            return Encoding.UTF8;
        }

        if (span.StartsWith(Utf32LittleEndianPreamble))
        {
            preambleLength = Utf32LittleEndianPreamble.Length;
            return Encoding.UTF32;
        }

        if (span.StartsWith(Utf16LittleEndianPreamble))
        {
            preambleLength = Utf16LittleEndianPreamble.Length;
            return Encoding.Unicode;
        }

        if (span.StartsWith(Utf16BigEndianPreamble))
        {
            preambleLength = Utf16BigEndianPreamble.Length;
            return Encoding.BigEndianUnicode;
        }

        preambleLength = 0;
        return Encoding.UTF8;
    }

    private static Encoding ResolveDeclaredEncoding(string charset)
    {
        // Quotes strip as a span, so the common quoted charset never allocates a substring.
        var name = charset.AsSpan();
        if (name.Length > 2 && name[0] is '"' && name[^1] is '"')
        {
            name = name[1..^1];
        }

        if (IsUtf8Name(name))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(name.ToString());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("The response content declared an invalid charset.", exception);
        }
    }

    /// <summary>The JSON wire's overwhelmingly common charset skips the encoding-name lookup.</summary>
    private static bool IsUtf8Name(ReadOnlySpan<char> name) =>
#if NET8_0_OR_GREATER
        Ascii.EqualsIgnoreCase(name, "utf-8");
#else
        name.Equals("utf-8".AsSpan(), StringComparison.OrdinalIgnoreCase);
#endif

    private static int GetPreambleLength(byte[] body, int count, Encoding encoding)
    {
        var span = body.AsSpan(0, count);
        switch (encoding.CodePage)
        {
            case Utf8CodePage:
                return span.StartsWith(Utf8Preamble) ? Utf8Preamble.Length : 0;
            case Utf32CodePage:
                return span.StartsWith(Utf32LittleEndianPreamble) ? Utf32LittleEndianPreamble.Length : 0;
            case UnicodeCodePage:
                return span.StartsWith(Utf16LittleEndianPreamble) ? Utf16LittleEndianPreamble.Length : 0;
            case BigEndianUnicodeCodePage:
                return span.StartsWith(Utf16BigEndianPreamble) ? Utf16BigEndianPreamble.Length : 0;
            default:
                var preamble = encoding.GetPreamble();
                return span.StartsWith(preamble) ? preamble.Length : 0;
        }
    }
}
