using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OpenCode.Sdk.Tools.Tests.CommentResidue;

/// <summary>
/// Reads the comments of the repository's code and configuration files, one line at a time. C# is
/// parsed, so a string never reads as a comment, and an inactive <c>#if</c> region's line comments
/// count too; MSBuild and other XML files give their XML comments; configuration, workflow and
/// shell files give their <c>#</c> comments; JavaScript and TypeScript give their line and block
/// comments. Any other file has no comments to read.
/// </summary>
internal static partial class CommentReader
{
    private static readonly string[] XmlExtensions = [".csproj", ".props", ".targets", ".slnx", ".config", ".xml"];
    private static readonly string[] HashExtensions = [".yml", ".yaml", ".editorconfig", ".globalconfig", ".gitattributes", ".gitignore", ".gitmodules", ".ps1", ".psm1", ".sh"];
    private static readonly string[] HashFileNames = ["CODEOWNERS"];
    private static readonly string[] ScriptExtensions = [".js", ".mjs", ".ts"];

    /// <summary>Whether the reader knows how comments look in this file.</summary>
    public static bool Reads(string path) =>
        path.EndsWith(".cs", StringComparison.Ordinal) || IsXml(path) || IsHash(path) || IsScript(path);

    public static IReadOnlyList<SourceLine> Read(string path, string text)
    {
        if (path.EndsWith(".cs", StringComparison.Ordinal))
        {
            return CSharp(text);
        }

        if (IsXml(path))
        {
            return Xml(text);
        }

        if (IsHash(path))
        {
            return Lines(text, HashComment());
        }

        return IsScript(path) ? Script(text) : [];
    }

    /// <summary>Every identifier token in a C# file, with its 1-based line.</summary>
    public static IEnumerable<SourceLine> CSharpIdentifiers(string text) =>
        CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantTokens()
            .Where(static token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(static token => new SourceLine(token.GetLocation().GetLineSpan().StartLinePosition.Line + 1, token.ValueText));

    private static List<SourceLine> CSharp(string text)
    {
        var result = new List<SourceLine>();
        foreach (var trivia in CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantTrivia(descendIntoTrivia: true))
        {
            var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                result.AddRange(Lines(trivia.ToString(), LineComment()).Select(comment => comment with { Line = comment.Line + line - 1 }));
            }
            else if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                result.AddRange(trivia.ToFullString().Split('\n').Select((part, index) => new SourceLine(line + index, part.TrimEnd('\r'))));
            }
        }

        return result;
    }

    private static List<SourceLine> Xml(string text) =>
    [
        .. XDocument.Parse(text, LoadOptions.SetLineInfo).DescendantNodes().OfType<XComment>()
            .SelectMany(static comment => comment.Value.Split('\n')
                .Select((part, index) => new SourceLine(((IXmlLineInfo)comment).LineNumber + index, part.TrimEnd('\r')))),
    ];

    private static List<SourceLine> Script(string text)
    {
        var result = Lines(text, LineComment());
        foreach (Match block in BlockComment().Matches(text))
        {
            var line = text.AsSpan(0, block.Index).Count('\n') + 1;
            result.AddRange(block.Value.Split('\n').Select((part, index) => new SourceLine(line + index, part.TrimEnd('\r'))));
        }

        return result;
    }

    private static List<SourceLine> Lines(string text, Regex comment) =>
    [
        .. text.Split('\n')
            .Select((line, index) => (Match: comment.Match(line), Line: index + 1))
            .Where(static pair => pair.Match.Success)
            .Select(static pair => new SourceLine(pair.Line, pair.Match.Value.TrimEnd('\r'))),
    ];

    private static bool IsXml(string path) => XmlExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal));

    private static bool IsHash(string path) =>
        HashExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal))
        || HashFileNames.Any(name => path.EndsWith("/" + name, StringComparison.Ordinal) || string.Equals(path, name, StringComparison.Ordinal));

    private static bool IsScript(string path) => ScriptExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal));

    [GeneratedRegex(@"(?:^|\s)#.*$", RegexOptions.None, 1000)]
    private static partial Regex HashComment();

    [GeneratedRegex(@"(?<![:/])//.*$", RegexOptions.None, 1000)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, 1000)]
    private static partial Regex BlockComment();
}
