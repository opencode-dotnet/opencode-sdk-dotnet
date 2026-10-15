using System.Security.Cryptography;
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The pinned CLI's earlier registration filename, <c>service-&lt;sha1(channel)&gt;.json</c>
/// (<c>packages/util/src/hash.ts</c>, <c>createHash("sha1")</c> over the raw channel string). The
/// digest names a file; it protects nothing, which is why this is the one file that may use it.
/// </summary>
internal static class ServiceLegacyFilename
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>Derives the legacy filename for a channel.</summary>
    /// <param name="channel">The raw channel name, not the sanitized form.</param>
    /// <returns>The filename.</returns>
    public static string For(string channel)
    {
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(channel));
        var hex = new char[digest.Length * 2];
        for (var i = 0; i < digest.Length; i++)
        {
            hex[i * 2] = HexDigits[digest[i] >> 4];
            hex[(i * 2) + 1] = HexDigits[digest[i] & 0x0F];
        }

        return "service-" + new string(hex) + ".json";
    }
}
