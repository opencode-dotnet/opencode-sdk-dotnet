using System.IO.Abstractions;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.CommentResidue;

/// <summary>
/// Scans the repository's code and configuration comments for what a reader of the code cannot
/// follow: a plan's task numbers, a design document's section numbers, paths into the
/// documentation, and the research log's question numbers. A comment states the rule itself
/// instead. The scan also refuses a switch that accepts snapshots without review, in C# code or in
/// any text file but Markdown, and a Stryker suppression comment that gives no reason.
/// <para>
/// The generated files the generator's manifest lists are left out: their comments come from the
/// emitters and the pinned specification. Files with no comment syntax the reader knows are not
/// read for comments: upstream patches, the generated reach report and snapshot files hold upstream
/// or generated text.
/// </para>
/// </summary>
internal sealed partial class CommentResidueScan(IFileSystem fileSystem, IReadOnlyList<string> repositoryFiles)
{
    internal const string TaskNumber = "a plan's task number";
    internal const string DesignSection = "a design document's section number";
    internal const string DocumentationPath = "a path into the documentation";
    internal const string ResearchLogNumber = "a research-log question number";
    internal const string SnapshotAutoAccept = "a switch that accepts snapshots without review";
    internal const string UnexplainedStrykerDisable = "a Stryker disable comment without a reason";

    private const string AutoAcceptIdentifier = "AutoVerify";
    private static readonly string[] UnreadExtensions = [".md", ".png", ".ico", ".snk", ".dll", ".exe", ".zip", ".nupkg"];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<IReadOnlyList<CommentResidueHit>> ReadAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var generated = await GeneratedFilesAsync(root, cancellationToken);
        var hits = new List<CommentResidueHit>();
        foreach (var file in repositoryFiles)
        {
            if (generated.Contains(file) || UnreadExtensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var text = await _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(root, file), cancellationToken);
            if (CommentReader.Reads(file))
            {
                hits.AddRange(CheckComments(file, CommentReader.Read(file, text)));
            }

            hits.AddRange(CheckAutoAccept(file, text));
        }

        return hits;
    }

    private async Task<IReadOnlySet<string>> GeneratedFilesAsync(string root, CancellationToken cancellationToken)
    {
        var manifest = _fileSystem.Path.Combine(root, GeneratedManifest.ManifestPath);
        if (!repositoryFiles.Contains(GeneratedManifest.ManifestPath, StringComparer.Ordinal) || !_fileSystem.File.Exists(manifest))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var files = new GeneratedManifest(await _fileSystem.File.ReadAllTextAsync(manifest, cancellationToken)).Files;
        return files.Select(static file => GeneratedManifest.SdkPath + file).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<CommentResidueHit> CheckComments(string file, IEnumerable<SourceLine> comments)
    {
        foreach (var comment in comments)
        {
            foreach (var (rule, pattern) in CommentRules())
            {
                foreach (Match match in pattern.Matches(comment.Text))
                {
                    yield return new CommentResidueHit(file, comment.Line, rule, match.Value);
                }
            }

            if (StrykerDisable().Match(comment.Text) is { Success: true } stryker && !HasReason(stryker.Groups["rest"].Value))
            {
                yield return new CommentResidueHit(file, comment.Line, UnexplainedStrykerDisable, comment.Text.Trim());
            }
        }
    }

    /// <summary>
    /// The auto-accept switches in one file: Verify's auto-accept identifier and its mentions in
    /// comments in C#, and any word spelling it in another text file.
    /// </summary>
    private static IEnumerable<CommentResidueHit> CheckAutoAccept(string file, string text)
    {
        var lines = file.EndsWith(".cs", StringComparison.Ordinal)
            ? CommentReader.CSharpIdentifiers(text).Where(static identifier => identifier.Text == AutoAcceptIdentifier)
                .Concat(CommentReader.Read(file, text).Where(static comment => AutoAcceptWord().IsMatch(comment.Text)))
            : text.Split('\n').Select((line, index) => new SourceLine(index + 1, line)).Where(static line => AutoAcceptWord().IsMatch(line.Text));
        return lines.Select(line => new CommentResidueHit(file, line.Line, SnapshotAutoAccept, line.Text.Trim()));
    }

    /// <summary>Whether a Stryker suppression's tail carries a reason after its colon, once a block comment's closing delimiter is set aside.</summary>
    private static bool HasReason(string rest)
    {
        var body = rest.Trim();
        if (body.EndsWith("*/", StringComparison.Ordinal))
        {
            body = body[..^2].TrimEnd();
        }

        var colon = body.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && body[(colon + 1)..].Trim().Length > 0;
    }

    private static IEnumerable<(string Rule, Regex Pattern)> CommentRules() =>
    [
        (TaskNumber, TaskPattern()),
        (DesignSection, DesignSectionPattern()),
        (DocumentationPath, DocumentationPathPattern()),
        (ResearchLogNumber, ResearchLogPattern()),
    ];

    [GeneratedRegex(@"\bTask [0-9]+\b", RegexOptions.None, 1000)]
    private static partial Regex TaskPattern();

    [GeneratedRegex(@"\bdesign §", RegexOptions.None, 1000)]
    private static partial Regex DesignSectionPattern();

    [GeneratedRegex(@"\bdocs/[A-Za-z0-9_./-]*\.md\b", RegexOptions.None, 1000)]
    private static partial Regex DocumentationPathPattern();

    [GeneratedRegex(@"\bQ[0-9]{3}\b", RegexOptions.None, 1000)]
    private static partial Regex ResearchLogPattern();

    [GeneratedRegex(@"(?<![A-Za-z])auto_?verify(?![A-Za-z])", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AutoAcceptWord();

    [GeneratedRegex(@"\bStryker\s*disable(?<rest>.*)$", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex StrykerDisable();
}
