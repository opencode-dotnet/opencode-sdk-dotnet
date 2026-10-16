using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenCode.Sdk.Tests.Sessions.Support;

/// <summary>Turns a typed <see cref="JsonNode"/> into a cloned <see cref="JsonElement"/> the drive protocol writers can embed.</summary>
internal static class DriveJson
{
    public static JsonElement Element(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
