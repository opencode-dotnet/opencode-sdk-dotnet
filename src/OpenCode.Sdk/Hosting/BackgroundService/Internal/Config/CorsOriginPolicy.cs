using System.Globalization;
using System.Net;

namespace OpenCode.Sdk.Internal;

/// <summary>The CLI's exact HTTP(S) origin and ECMAScript item-trimming policy.</summary>
internal static class CorsOriginPolicy
{
    private static readonly char[] TrimCharacters =
        ['\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u0020', '\u00a0', '\u1680',
            '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a',
            '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff'];

    /// <summary>Trims exactly JavaScript WhiteSpace and LineTerminator code points.</summary>
    /// <param name="value">One comma-list item.</param>
    /// <returns>The trimmed item.</returns>
    public static string TrimItem(string value) => value.Trim(TrimCharacters);

    /// <summary>Tests whether the item equals its canonical HTTP(S) URL origin.</summary>
    /// <param name="origin">The already-trimmed item.</param>
    /// <returns>Whether the CLI accepts it unchanged as an origin.</returns>
    public static bool IsOrigin(string origin)
    {
        string? asciiHost = null;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            var schemeEnd = origin.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                return false;
            }

            var hostStart = schemeEnd + 3;
            var portStart = origin.AsSpan(hostStart).IndexOf(':');
            var hostEnd = portStart < 0 ? origin.Length : hostStart + portStart;

            asciiHost = origin[hostStart..hostEnd];
            if (asciiHost.Length == 0 || !asciiHost.All(static character =>
                character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_'
                || "!$&'()*+;=~\"`{}".Contains(character, StringComparison.Ordinal)))
            {
                return false;
            }

            // The BCL parses the scheme and port; the final exact comparison still decides every verdict.
            var surrogate = origin[..hostStart] + "example.com" + origin[hostEnd..];
            if (!Uri.TryCreate(surrogate, UriKind.Absolute, out uri))
            {
                return false;
            }
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = asciiHost ?? uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            if (host.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(host, out var address))
            {
                return false;
            }

            host = "[" + SerializeIpv6(address) + "]";
        }
        else if (uri.HostNameType != UriHostNameType.IPv4 && !IsDomain(host))
        {
            return false;
        }

        var canonical = uri.Scheme + "://" + host
            + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        return string.Equals(origin, canonical, StringComparison.Ordinal);
    }

    private static bool IsDomain(string host)
    {
        var labels = host.Split('.');
        var last = labels.Length > 1 && labels[^1].Length == 0 ? labels[^2] : labels[^1];
        // A numeric final label invokes WHATWG's IPv4 parser, not DNS, even when Uri accepts DNS.
        if (last.Length > 0 && (last.All(static character => character is >= '0' and <= '9')
            || (last.StartsWith("0x", StringComparison.Ordinal) && last.Skip(2).All(Uri.IsHexDigit))))
        {
            return false;
        }

        try
        {
            var idn = new IdnMapping();
            return labels.Where(static label => label.StartsWith("xn--", StringComparison.Ordinal))
                .All(label => string.Equals(idn.GetAscii(idn.GetUnicode(label)), label, StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string SerializeIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var groups = Enumerable.Range(0, 8).Select(index => (bytes[index * 2] << 8) | bytes[(index * 2) + 1]).ToArray();
        var start = -1;
        var length = 1;
        for (var index = 0; index < groups.Length; index++)
        {
            var end = index;
            while (end < groups.Length && groups[end] == 0)
            {
                end++;
            }

            if (end - index > length)
            {
                start = index;
                length = end - index;
            }
        }

        var text = groups.Select(static group => group.ToString("x", CultureInfo.InvariantCulture)).ToArray();
        return start < 0 ? string.Join(':', text)
            : string.Join(':', text.Take(start)) + "::" + string.Join(':', text.Skip(start + length));
    }
}
