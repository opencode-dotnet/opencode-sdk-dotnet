using System.Security.Cryptography;

namespace OpenCode.Sdk.Internal;

/// <summary>Generates the private credentials shared by the launcher and service configuration.</summary>
internal sealed class RandomCredential(RandomNumberGenerator random)
{
    /// <summary>The launcher's 32-byte, unpadded base64url credential.</summary>
    /// <returns>The generated password.</returns>
    public string Password()
    {
        var bytes = new byte[32];
        random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The CLI's 64-bit lowercase-hex remote route.</summary>
    /// <returns>The generated route.</returns>
    public string Route()
    {
        var bytes = new byte[8];
        random.GetBytes(bytes);
        return string.Concat(bytes.Select(static value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
