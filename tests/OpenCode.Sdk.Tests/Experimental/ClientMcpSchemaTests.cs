using System.Text.Json.Serialization;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed partial class ClientMcpSchemaTests
{
    [Test]
    public async Task FromType_Should_Export_The_Source_Generated_Object_Contract()
    {
        var schema = ClientMcpSchema.FromType(LookupJsonContext.Default.LookupArguments);
        await Assert.That(schema.EnumerateObject().Select(static member => member.Name))
            .IsEquivalentTo(["type", "properties", "required"], CollectionOrdering.Matching);
        await Assert.That(schema.GetProperty("type").GetString()).IsEqualTo("object");
        var properties = schema.GetProperty("properties");
        await Assert.That(properties.EnumerateObject().Select(static member => member.Name))
            .IsEquivalentTo(["id", "Nested"], CollectionOrdering.Matching);
        await Assert.That(properties.GetProperty("id").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(schema.GetProperty("required").EnumerateArray().Select(static item => item.ToString()))
            .IsEquivalentTo(["id"], CollectionOrdering.Matching);
        await Assert.That(properties.GetProperty("Nested").GetProperty("type").EnumerateArray().Select(static item => item.ToString()))
            .IsEquivalentTo(["object", "null"], CollectionOrdering.Matching);
        await Assert.That(properties.GetProperty("Nested").GetProperty("additionalProperties").GetProperty("type")
            .EnumerateArray().Select(static item => item.ToString()))
            .IsEquivalentTo(["string", "null"], CollectionOrdering.Matching);
        var dictionary = ClientMcpSchema.FromType(LookupJsonContext.Default.DictionaryStringString);
        await Assert.That(dictionary.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(dictionary.GetProperty("additionalProperties").GetProperty("type").EnumerateArray().Select(static item => item.ToString()))
            .IsEquivalentTo(["string", "null"], CollectionOrdering.Matching);
        var primitive = await Assert.That(() => ClientMcpSchema.FromType(LookupJsonContext.Default.Int32)).Throws<NotSupportedException>();
        await Assert.That(primitive!.Message).IsEqualTo("Tool input schemas require object or dictionary metadata.");
        var array = await Assert.That(() => ClientMcpSchema.FromType(LookupJsonContext.Default.StringArray)).Throws<NotSupportedException>();
        await Assert.That(array!.Message).IsEqualTo("Tool input schemas require object or dictionary metadata.");
    }

    internal sealed record LookupArguments
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }
        public IReadOnlyDictionary<string, string?>? Nested { get; init; }
    }

    [JsonSerializable(typeof(LookupArguments))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(Dictionary<string, string?>))]
    internal sealed partial class LookupJsonContext : JsonSerializerContext;
}
