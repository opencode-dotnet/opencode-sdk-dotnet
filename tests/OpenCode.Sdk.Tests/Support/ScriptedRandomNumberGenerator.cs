using System.Security.Cryptography;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>A resource-free BCL randomness seam; each fill takes the next scripted bytes.</summary>
internal sealed class ScriptedRandomNumberGenerator : RandomNumberGenerator
{
    private int _offset;
    public List<int> Requests { get; } = [];
    public override void GetBytes(byte[] data)
    {
        Requests.Add(data.Length);
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = unchecked((byte)(_offset++ * 17));
        }
    }
    public override void GetNonZeroBytes(byte[] data) => throw new NotSupportedException();
}
