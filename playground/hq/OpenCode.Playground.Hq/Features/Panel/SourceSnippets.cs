namespace OpenCode.Playground.Hq.Features.Panel;

/// <summary>Reads the panel's marked regions from HQ's own source, embedded at build time.</summary>
internal sealed class SourceSnippets
{
    private const string RepositoryFolder = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/playground/hq/OpenCode.Playground.Hq/";
    private readonly System.Reflection.Assembly _assembly = typeof(SourceSnippets).Assembly;

    public static Uri Link(string file) => new(RepositoryFolder + file);

    /// <summary>Reads one region without its markers or common indentation.</summary>
    public async Task<string> ReadAsync(string file, string region)
    {
        var name = _assembly.GetManifestResourceNames().SingleOrDefault(name => name.Replace('\\', '/') == "hq/" + file)
            ?? throw new InvalidOperationException($"{file} is not embedded.");
        await using var stream = _assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{file} is not embedded.");
        using var reader = new StreamReader(stream);
        var lines = (await reader.ReadToEndAsync()).ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == "#region panel: " + region);
        var end = start < 0 ? -1 : Array.FindIndex(lines, start, static line => line.Trim() == "#endregion");
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException($"{file} has no complete region 'panel: {region}'.");
        }

        var body = lines[(start + 1)..end];
        var indent = body.Where(static line => line.Trim().Length > 0).Min(static line => line.Length - line.TrimStart().Length);
        return string.Join('\n', body.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart()));
    }
}
