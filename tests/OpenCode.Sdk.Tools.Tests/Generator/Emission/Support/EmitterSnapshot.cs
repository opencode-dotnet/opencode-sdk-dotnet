using System.Text;
using OpenCode.Sdk.Tools.Generator.Emission;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal static class EmitterSnapshot
{
    public static string Create(IReadOnlyList<GeneratedSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var result = new StringBuilder();
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            _ = result.Append("// File: ").Append(source.RelativePath).Append('\n');
            _ = result.Append(Encoding.UTF8.GetString(source.Utf8Source.Span));
            if (index < sources.Count - 1)
            {
                _ = result.Append('\n');
            }
        }

        return result.ToString();
    }

    /// <summary>One emitted file's text, for assertions that read a single source rather than the joined snapshot.</summary>
    public static string Content(IReadOnlyList<GeneratedSource> sources, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var source = sources.Single(candidate => string.Equals(candidate.RelativePath, relativePath, StringComparison.Ordinal));
        return Encoding.UTF8.GetString(source.Utf8Source.Span);
    }
}
