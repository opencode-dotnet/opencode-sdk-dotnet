using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class JsonBodyWriterTests
{
    [Test]
    [Arguments("short")]
    [Arguments("escaped")]
    [Arguments("metadata")]
    [Arguments("large")]
    [Arguments("empty")]
    public async Task Write_Should_Match_SerializeToUtf8Bytes(string shape)
    {
        var body = BodyFor(shape);
        var typeInfo = OpenCodeJsonContext.Default.GetTypeInfo(body.GetType())!;
        var writer = JsonBodyWriter.Create(typeInfo.Options);
        var pool = new TrackingByteArrayPool();

        using var content = writer.Write(body, typeInfo, pool);
        await AssertSerializedBodyAsync(content, body, typeInfo);
    }

    [Test]
    public async Task Write_Should_Reset_Between_Different_Bodies()
    {
        var typeInfo = OpenCodeJsonContext.Default.SessionPromptRequest;
        var writer = JsonBodyWriter.Create(typeInfo.Options);
        var pool = new TrackingByteArrayPool();
        var first = new SessionPromptRequest { Text = new string('x', 2048) };
        var second = new SessionPromptRequest { Text = "second" };

        using var firstContent = writer.Write(first, typeInfo, pool);
        using var secondContent = writer.Write(second, typeInfo, pool);

        await Assert.That(await firstContent.ReadAsByteArrayAsync())
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(first, typeInfo), CollectionOrdering.Matching);
        await Assert.That(await secondContent.ReadAsByteArrayAsync())
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(second, typeInfo), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Write_Should_Clear_Grown_Storage_And_Keep_The_Last_Rent_Until_Disposal()
    {
        var returned = new List<byte[]>();
        var pool = new TrackingByteArrayPool(onReturn: buffer => returned.Add([.. buffer]));
        var typeInfo = OpenCodeJsonContext.Default.SessionPromptRequest;
        var body = new SessionPromptRequest { Text = new string('x', 2 * 1024 * 1024) };

        using var content = WriteBody(body, typeInfo, pool);
        await AssertSerializedBodyAsync(content, body, typeInfo);
        await Assert.That(pool.RentCount).IsGreaterThan(1);
        await Assert.That(pool.ReturnCount).IsEqualTo(pool.RentCount - 1);
        await Assert.That(pool.OutstandingCount).IsEqualTo(1);
        // Tracking rents start at 0xff. A returned growth buffer may keep untouched spare
        // capacity, but each byte the writer changed must already be zero at Return.
        foreach (var buffer in returned)
        {
            await Assert.That(buffer.Where(static value => value != 0xff).ToArray())
                .IsEquivalentTo(new byte[buffer.Count(static value => value != 0xff)], CollectionOrdering.Matching);
        }

        content.Dispose();

        await Assert.That(pool.ReturnCount).IsEqualTo(pool.RentCount);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task Write_Should_Clear_All_Storage_And_Propagate_A_Converter_Failure_Then_Reset()
    {
        var returned = new List<byte[]>();
        var pool = new TrackingByteArrayPool(onReturn: buffer => returned.Add([.. buffer]));
        var options = new JsonSerializerOptions { TypeInfoResolver = OpenCodeJsonContext.Default };
        var typeInfo = JsonMetadataServices.CreateValueInfo<string>(options, new FailingConverter());
        var writer = JsonBodyWriter.Create(options);

        var exception = await Assert.That(() => writer.Write("fail", typeInfo, pool)).Throws<InvalidOperationException>();

        await Assert.That(exception?.Message).IsEqualTo(FailingConverter.FailureMessage);
        await Assert.That(pool.RentCount).IsGreaterThan(0);
        await Assert.That(pool.ReturnCount).IsEqualTo(pool.RentCount);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
        foreach (var buffer in returned)
        {
            await Assert.That(buffer).IsEquivalentTo(new byte[buffer.Length], CollectionOrdering.Matching);
        }

        using var content = writer.Write("good", typeInfo, pool);
        await Assert.That(await content.ReadAsByteArrayAsync())
            .IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes("good", typeInfo), CollectionOrdering.Matching);
    }

    private static async Task AssertSerializedBodyAsync(HttpContent content, object body, JsonTypeInfo typeInfo)
    {
        var bytes = await content.ReadAsByteArrayAsync();

        await Assert.That(bytes).IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(body, typeInfo), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Write_Should_Copy_Bytes_Before_Returning_Each_Grown_Array()
    {
        using var value = JsonDocument.Parse("7");
        var body = new SessionPromptRequest
        {
            Text = "growth",
            Metadata = Enumerable.Range(0, 128).ToDictionary(index => new string('x', index + 32),
                _ => value.RootElement.Clone(), StringComparer.Ordinal),
        };
        var typeInfo = OpenCodeJsonContext.Default.SessionPromptRequest;
        var pool = new TrackingByteArrayPool(onReturn: buffer => buffer.AsSpan().Fill(0xAB));

        using var content = WriteBody(body, typeInfo, pool);

        await AssertSerializedBodyAsync(content, body, typeInfo);
        await Assert.That(pool.RentCount).IsGreaterThan(3);
    }

    [Test]
    public async Task Write_Should_Match_All_Custom_Writer_Options()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = OpenCodeJsonContext.Default,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            IndentCharacter = '\t',
            IndentSize = 3,
            NewLine = Environment.NewLine == "\n" ? "\r\n" : "\n",
        };
        var typeInfo = new OpenCodeJsonContext(options).SessionPromptRequest;
        var writer = JsonBodyWriter.Create(options);
        var body = BodyFor("escaped");
        using var content = writer.Write(body, typeInfo, new TrackingByteArrayPool());

        await AssertSerializedBodyAsync(content, body, typeInfo);
    }

    [Test]
    public async Task Write_Should_Refuse_Depth_Past_64_With_The_Serializers_Exception_Type()
    {
        using var value = JsonDocument.Parse(new string('[', 65) + "0" + new string(']', 65),
            new JsonDocumentOptions { MaxDepth = 128 });
        var body = new SessionPromptRequest
        {
            Text = "depth",
            Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["nested"] = value.RootElement.Clone() },
        };
        var typeInfo = OpenCodeJsonContext.Default.SessionPromptRequest;
        var expected = await Assert.That(() => JsonSerializer.SerializeToUtf8Bytes(body, typeInfo)).Throws<Exception>();
        var writer = JsonBodyWriter.Create(typeInfo.Options);

        var actual = await Assert.That(() => writer.Write(body, typeInfo, new TrackingByteArrayPool())).Throws<Exception>();

        await Assert.That(actual!.GetType()).IsEqualTo(expected!.GetType());
    }

    private static PooledJsonContent WriteBody(object body, JsonTypeInfo typeInfo, TrackingByteArrayPool pool) =>
        JsonBodyWriter.Create(typeInfo.Options).Write(body, typeInfo, pool);

    private static object BodyFor(string shape)
    {
        using var value = JsonDocument.Parse("7");
        return shape switch
        {
            "short" => new SessionPromptRequest { Text = "hello" },
            "escaped" => new SessionPromptRequest { Text = "Türkçe 😀 <&\"\n" },
            "metadata" => new SessionPromptRequest
            {
                Text = "metadata",
                Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["é\"\n"] = value.RootElement.Clone() },
            },
            "large" => new SessionPromptRequest { Text = new string('x', 2 * 1024 * 1024) },
            "empty" => new SessionCreateRequest(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private sealed class FailingConverter : JsonConverter<string>
    {
        public const string FailureMessage = "The test converter failed after writing a partial body.";

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("text", value);
            if (value == "fail")
            {
                throw new InvalidOperationException(FailureMessage);
            }

            writer.WriteEndObject();
        }
    }
}
