using System.Runtime.InteropServices;
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>Holds either validated UTF-8 bytes or a body decoded through its declared/BOM encoding.</summary>
internal readonly record struct EncodedResponseBody(ReadOnlyMemory<byte> Utf8Body, string? DecodedBody)
{
    public string GetDecodedBody()
    {
        if (DecodedBody is { } decoded)
        {
            return decoded;
        }

        if (Utf8Body.IsEmpty)
        {
            return string.Empty;
        }

        return MemoryMarshal.TryGetArray(Utf8Body, out var segment) && segment.Array is not null
            ? Encoding.UTF8.GetString(segment.Array, segment.Offset, segment.Count)
            : Encoding.UTF8.GetString(Utf8Body.ToArray());
    }
}
