using System.Text.Json;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>Reads <c>.slopwatch/baseline.json</c>: one suppression per entry, scoped to the file the entry names.</summary>
internal static class SlopwatchBaselineReader
{
    public static IReadOnlyList<Suppression> Read(string origin, string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{origin} has no 'entries' array.");
        }

        return
        [
            .. entries.EnumerateArray().Select(entry => new Suppression(
                SuppressionChannel.SlopwatchBaseline,
                origin,
                Required(origin, entry, "filePath").Replace('\\', '/'),
                Required(origin, entry, "ruleId"),
                string.Empty)),
        ];
    }

    private static string Required(string origin, JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidOperationException($"Every entry in {origin} must carry a nonempty '{name}'.");
}
