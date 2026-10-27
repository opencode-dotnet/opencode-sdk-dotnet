using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Builds UTF-8 frames and takes owned snapshots while a reader's data is valid.</summary>
internal static class TestFrames
{
    public static ServerSentEvent FromText(string name, string data) => new(name, Encoding.UTF8.GetBytes(data));

    public static string ReadText(ReadOnlyMemory<byte> data) => Encoding.UTF8.GetString(data.Span);

    public static ServerSentEvent Copy(ServerSentEvent frame) => frame with { Data = frame.Data.ToArray() };
}
