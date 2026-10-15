using System.Text.Json;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// The generator's manifest of every generated SDK file, by path relative to the SDK project. It is
/// the one authority on which files are generated.
/// </summary>
internal sealed class GeneratedManifest
{
    /// <summary>The SDK project folder, relative to the repository root, that manifest paths are relative to.</summary>
    internal const string SdkPath = "src/OpenCode.Sdk/";

    /// <summary>The manifest itself, relative to the repository root.</summary>
    internal const string ManifestPath = SdkPath + ".generated-manifest.json";

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
