using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class UnionArmTests
{
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
        long[] expected = [];
        long[] actual = [];
        var arm = UnionArm<ISessionMessageToolState>.Of(Refuse);
        var failure = await Assert.That(ReadAndCapture)
            .Throws<FormatException>();
        await Assert.That(failure).IsSameReferenceAs(original);
        await Assert.That(actual).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        SessionMessageToolStateCompleted Refuse(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            _ = reader.Read();
            _ = reader.Read();
            expected = Position(ref reader);
            throw original;
        }

        void ReadAndCapture()
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(new FixtureLoader().LoadJson("Serialization.json-element-dictionary-empty-object.json")));
            _ = reader.Read();
            try
            {
                _ = arm.Read(ref reader);
            }
            finally
            {
                actual = Position(ref reader);
            }
        }
    }

    [Test]
    public async Task Read_Should_Rewind_For_The_Canonical_Metadata_Failure()
    {
        const string fixture = "Serialization.json-element-dictionary-array-token.json";
        var metadata = await Assert.That(() => ReadMetadataFixture(fixture))
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

    private static SessionMessageToolStateCompleted? ReadMetadataFixture(string fixture)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(new FixtureLoader().LoadJson(fixture)));
        _ = reader.Read();
        return JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.SessionMessageToolStateCompleted);
    }

    private static long[] Position(ref Utf8JsonReader reader) => [reader.BytesConsumed, (long)reader.TokenType, reader.CurrentDepth, reader.TokenStartIndex];
}
