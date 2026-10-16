using System.Globalization;
using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>A test source file over 800 physical lines.</summary>
internal static class FileSizeGuard
{
    public const string Rule = "File size";
    public const string Text = "A test source file over 800 physical lines.";

    public static IReadOnlyList<RepositoryGateSite> Find(TestSourceCorpus corpus)
    {
        return [.. from file in corpus.Files
                   let lines = file.Text.Lines.Count - (file.Text.Lines[^1].Span.IsEmpty ? 1 : 0)
                   where lines > 800
                   select new RepositoryGateSite(file.Path, 801, 0, Rule,
                       string.Create(CultureInfo.InvariantCulture, $"{Text} Found {lines} physical lines."))];
    }
}
