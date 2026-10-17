using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

/// <summary>
/// An open model keeps every wire member the document leaves open: the members no named property
/// claims land in <c>AdditionalProperties</c> by wire name and are written back on serialization,
/// on every target framework the serializer's extension-data path runs on.
/// </summary>
public sealed class OpenModelRoundTripTests
{
    private readonly GeneratedJsonSerializer _serializer = new();

    [Test]
    public async Task ProviderSettings_Should_Keep_Open_Members_Through_A_Round_Trip()
    {
        const string json = """{"chunkTimeout":5,"baseURL":"https://llm.example.test","headers":{"x-team":"sdk"},"retries":3}""";

        var settings = _serializer.Deserialize<ProviderSettings>(json);

        await Assert.That(settings.ChunkTimeout!.Kind).IsEqualTo(ProviderSettingsChunkTimeoutKind.Number);
        await Assert.That(settings.ChunkTimeout.Number).IsEqualTo(5);
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert.That(settings.AdditionalProperties.Keys).IsEquivalentTo(["baseURL", "headers", "retries"], CollectionOrdering.Any);
        await Assert.That(settings.AdditionalProperties["baseURL"].GetString()).IsEqualTo("https://llm.example.test");
        await AssertRoundTripAsync(json, _serializer.Serialize(settings));
    }

    [Test]
    public async Task ProviderSettings_Should_Read_A_Disabled_Timeout_As_The_Boolean_Arm()
    {
        const string json = """{"chunkTimeout":false,"headerTimeout":30000}""";

        var settings = _serializer.Deserialize<ProviderSettings>(json);

        await Assert.That(settings.ChunkTimeout!.Kind).IsEqualTo(ProviderSettingsChunkTimeoutKind.Boolean);
        await Assert.That(settings.ChunkTimeout.Boolean).IsFalse();
        await Assert.That(settings.HeaderTimeout!.Kind).IsEqualTo(ProviderSettingsHeaderTimeoutKind.Number);
        await Assert.That(settings.HeaderTimeout.Number).IsEqualTo(30000);
        await Assert.That(settings.AdditionalProperties).IsEmpty();
        await AssertRoundTripAsync(json, _serializer.Serialize(settings));
    }

    /// <summary>A member the document names is read into its property and never into the open view.</summary>
    [Test]
    public async Task ProviderSettings_Should_Read_Encoding_Into_Its_Named_Member()
    {
        const string json = """{"encoding":"gzip","baseURL":"https://llm.example.test"}""";

        var settings = _serializer.Deserialize<ProviderSettings>(json);

        await Assert.That(settings.Encoding).IsEqualTo(ProviderEncoding.Gzip);
        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert.That(settings.AdditionalProperties.Keys).IsEquivalentTo(["baseURL"], CollectionOrdering.Any);
        await AssertRoundTripAsync(json, _serializer.Serialize(settings));
    }

    [Test]
    public async Task ModelSettings_Should_Keep_Open_Members_Through_A_Round_Trip()
    {
        const string json = """{"reasoningEffort":"high","temperature":0.2}""";

        var settings = _serializer.Deserialize<ModelSettings>(json);

        // Dictionary/set membership is the contract; enumeration order is not.
        await Assert.That(settings.AdditionalProperties.Keys).IsEquivalentTo(["reasoningEffort", "temperature"], CollectionOrdering.Any);
        await AssertRoundTripAsync(json, _serializer.Serialize(settings));
    }

    [Test]
    public async Task An_Open_Model_Without_Open_Members_Should_Expose_An_Empty_View()
    {
        var settings = _serializer.Deserialize<ProviderSettings>("""{"chunkTimeout":5}""");

        await Assert.That(settings.AdditionalProperties).IsEmpty();
        await AssertRoundTripAsync("""{"chunkTimeout":5}""", _serializer.Serialize(settings));
    }

    private static async Task AssertRoundTripAsync(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        await Assert.That(actualDocument.RootElement).IsEqualTo(expectedDocument.RootElement, EqualityComparer<JsonElement>.Create(JsonElement.DeepEquals))
            .Because($"expected JSON: {expectedDocument.RootElement.GetRawText()}; actual JSON: {actualDocument.RootElement.GetRawText()}; {actual}");
    }
}
