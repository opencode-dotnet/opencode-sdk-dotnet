using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Models;

public sealed class SpecMediaTypeTests
{
    [Test]
    [Arguments("application/json; charset=utf-8", "application/json", true, false)]
    [Arguments("APPLICATION/PROBLEM+JSON", "application/problem+json", true, false)]
    [Arguments("Text/Event-Stream", "text/event-stream", false, true)]
    public async Task Create_Should_Normalize_And_Classify_Media_Types(string raw, string stripped, bool isJson, bool isEventStream)
    {
        var mediaType = SpecMediaType.Create(raw);

        await Assert.That(mediaType.Raw).IsEqualTo(raw);
        await Assert.That(mediaType.Stripped).IsEqualTo(stripped);
        await Assert.That(mediaType.IsJson).IsEqualTo(isJson);
        await Assert.That(mediaType.IsEventStream).IsEqualTo(isEventStream);
    }

    [Test]
    public async Task Create_Should_Refuse_Malformed_Media_Type()
    {
        var action = () => SpecMediaType.Create("application-json");

        await Assert.That(action).Throws<ArgumentException>();
    }
}
