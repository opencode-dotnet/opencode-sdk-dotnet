using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class UnionArmTests
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

    [Test]
    public async Task Of_Should_Keep_The_Arm_Type_And_Supply_Its_Own_Metadata_Options()
    {
        JsonSerializerOptions? received = null;
        var arm = UnionArm<ISessionMessageToolState>.Of(Read);
        await Assert.That(arm.Type).IsEqualTo(typeof(SessionMessageToolStateCompleted));
        var value = ReadFixture(arm, "Serialization.json-element-dictionary-empty-object.json");
        await Assert.That(value).IsTypeOf<SessionMessageToolStateCompleted>();
        await Assert.That(((SessionMessageToolStateCompleted)value!).Metadata!["after"].GetBoolean()).IsTrue();
        await Assert.That(received).IsSameReferenceAs(OpenCodeJsonContext.Default.SessionMessageToolStateCompleted.Options);

        SessionMessageToolStateCompleted Read(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            received = options;
            return JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.SessionMessageToolStateCompleted)
                ?? throw new JsonException("Expected completed state.");
        }
    }

    [Test]
    public async Task Read_Should_Not_Repair_A_False_Reader_Refusal()
    {
        var original = new FormatException("reader-refusal");
        var arm = UnionArm<ISessionMessageToolState>.Of(Refuse);
        var failure = await Assert.That(() => ReadFixture(arm, "Serialization.json-element-dictionary-empty-object.json"))
            .Throws<FormatException>();
        await Assert.That(failure).IsSameReferenceAs(original);

        SessionMessageToolStateCompleted Refuse(ref Utf8JsonReader reader, JsonSerializerOptions options) => throw original;
    }

    [Test]
    public async Task Read_Should_Rewind_For_The_Canonical_Metadata_Failure()
    {
        const string fixture = "Serialization.json-element-dictionary-array-token.json";
        var metadata = await Assert.That(() => ReadFixture(UnionArm<ISessionMessageToolState>.Of<SessionMessageToolStateCompleted>(), fixture))
            .Throws<JsonException>();
        var arm = UnionArm<ISessionMessageToolState>.Of(Refuse);
        var failure = await Assert.That(() => ReadFixture(arm, fixture)).Throws<JsonException>();
        await Assert.That(failure!.Message).IsEqualTo(metadata!.Message);
        await Assert.That(failure.Path).IsEqualTo(metadata.Path);
        await Assert.That(failure.LineNumber).IsEqualTo(metadata.LineNumber);
        await Assert.That(failure.BytePositionInLine).IsEqualTo(metadata.BytePositionInLine);

        static SessionMessageToolStateCompleted Refuse(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            _ = reader.Read();
            _ = reader.Read();
            throw new JsonException("reader-refusal");
        }
    }

    private static ISessionMessageToolState? ReadFixture(UnionArm<ISessionMessageToolState> arm, string fixture)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(new FixtureLoader().LoadJson(fixture)));
        _ = reader.Read();
        return arm.Read(ref reader);
    }
}
