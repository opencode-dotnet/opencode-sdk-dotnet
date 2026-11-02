using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class UnionMemberReaderTests
{
    [Test]
    public async Task Read_Should_Match_Binary_Metadata_For_Null_And_Base64()
    {
        var info = OpenCodeJsonContext.Default.GetTypeInfo(typeof(ReadOnlyMemory<byte>))
            as System.Text.Json.Serialization.Metadata.JsonTypeInfo<ReadOnlyMemory<byte>>;
        await Assert.That(info).IsNotNull();
        foreach (var isNull in new[] { true, false })
        {
            var json = isNull ? "null"u8.ToArray() : "\"Y29udHJvbA==\""u8.ToArray();
            var expected = ReadBinary(json, false);
            var actual = ReadBinary(json, true);
            await Assert.That(actual.Value).IsEquivalentTo(expected.Value, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(actual.Value).IsEquivalentTo(isNull ? [] : Encoding.UTF8.GetBytes("control"), TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(actual.Consumed).IsEqualTo(expected.Consumed);
            await Assert.That(actual.Token).IsEqualTo(expected.Token);
            await Assert.That(actual.Depth).IsEqualTo(expected.Depth);
            await Assert.That(actual.Start).IsEqualTo(expected.Start);
        }
    }

    private static (byte[] Value, long Consumed, JsonTokenType Token, int Depth, long Start) ReadBinary(byte[] json, bool member)
    {
        var reader = new Utf8JsonReader(json);
        _ = reader.Read();
        var options = OpenCodeJsonContext.Default.Options;
        var info = OpenCodeJsonContext.Default.GetTypeInfo(typeof(ReadOnlyMemory<byte>))
            as System.Text.Json.Serialization.Metadata.JsonTypeInfo<ReadOnlyMemory<byte>>
            ?? throw new InvalidOperationException("Missing binary metadata.");
        var value = member ? UnionMemberReader.Read<ReadOnlyMemory<byte>>(ref reader, options)
            : JsonSerializer.Deserialize(ref reader, info);
        return (value.ToArray(), reader.BytesConsumed, reader.TokenType, reader.CurrentDepth, reader.TokenStartIndex);
    }
}
