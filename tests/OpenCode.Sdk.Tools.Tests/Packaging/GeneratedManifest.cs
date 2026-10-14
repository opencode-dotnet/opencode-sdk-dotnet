using System.Text.Json;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

internal sealed class GeneratedManifest
{
    public GeneratedManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = entry.GetString();
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".cs", StringComparison.Ordinal)
                || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)
                || path.Split('/').Any(static segment => segment is "" or "." or "..") || !files.Add(path))
            {
                throw new InvalidOperationException($"Generated manifest entry '{path}' must be a unique relative C# path.");
            }
        }

        Files = files;
    }

    public IReadOnlySet<string> Files { get; }
}
