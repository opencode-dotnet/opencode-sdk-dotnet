using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions.Support;

internal sealed class SessionMessageEvidenceProjector
{
    private readonly GeneratedJsonSerializer _serializer = new();

    public IReadOnlyList<SessionMessageEvidence> Project(IEnumerable<ISessionMessageInfo> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var evidence = new List<SessionMessageEvidence>();
        foreach (var message in messages)
        {
            using var document = JsonDocument.Parse(_serializer.Serialize(message));
            var root = document.RootElement;
            evidence.Add(new SessionMessageEvidence(
                root.GetProperty("type").GetString()!,
                root.GetProperty("id").GetString()!,
                ReadText(root),
                root.GetProperty("time").GetProperty("created").GetDouble()));
        }

        return evidence;
    }

    private static string ReadText(JsonElement message)
    {
        if (message.TryGetProperty("text", out var text))
        {
            return text.GetString()!;
        }

        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return string.Concat(content.EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "text")
            .Select(item => item.GetProperty("text").GetString()));
    }
}
