using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OpenCode.Sdk.Tools.Tests.CommentResidue;

/// <summary>
/// Reads the comments of the repository's code and configuration files, one line at a time.
/// <list type="bullet">
/// <item>C# is parsed, so a string never reads as a comment; an inactive <c>#if</c> region is
/// parsed as C# too.</item>
/// <item>MSBuild and other XML files give their XML comments.</item>
/// <item>Configuration, workflow, shell and PowerShell files give their <c>#</c> comments: a
/// <c>#</c> at the start of a line or after a space, outside a quoted string. PowerShell also gives
/// its <c>&lt;# … #&gt;</c> blocks.</item>
/// <item>JavaScript and TypeScript are lexed: strings, template literals and regular expression
/// literals are skipped, and line and block comments are read.</item>
/// </list>
/// A script embedded in a workflow's <c>run:</c> block is read with the workflow's <c>#</c> rule
/// only. Any other file has no comments to read.
/// </summary>
internal static partial class CommentReader
{
    private const char NoCharacter = '\0';
    private const char LineFeed = '\n';
    private const char CarriageReturn = '\r';
    private const char Backslash = '\\';

    private static readonly string[] XmlExtensions = [".csproj", ".props", ".targets", ".slnx", ".config", ".xml"];
    private static readonly string[] HashExtensions = [".yml", ".yaml", ".editorconfig", ".globalconfig", ".gitattributes", ".gitignore", ".gitmodules", ".ps1", ".psm1", ".sh"];
    private static readonly string[] PowerShellExtensions = [".ps1", ".psm1"];
    private static readonly string[] HashFileNames = ["CODEOWNERS", "BannedSymbols.txt"];
    private static readonly string[] ScriptExtensions = [".js", ".mjs", ".ts"];

    /// <summary>Whether the reader knows how comments look in this file.</summary>
    public static bool Reads(string path) =>
        path.EndsWith(".cs", StringComparison.Ordinal) || IsXml(path) || IsHash(path) || IsScript(path);

    public static IReadOnlyList<SourceLine> Read(string path, string text)
    {
        if (path.EndsWith(".cs", StringComparison.Ordinal))
        {
            var comments = new List<SourceLine>();
            ReadCSharp(text, 0, comments, identifiers: null);
            return comments;
        }

        if (IsXml(path))
        {
            return Xml(text);
        }

        if (IsHash(path))
        {
            var comments = HashComments(text);
            if (PowerShellExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal)))
            {
                comments.AddRange(Blocks(text, PowerShellBlock()));
            }

            return comments;
        }

        return IsScript(path) ? ScriptComments(text) : [];
    }

    /// <summary>Every identifier token in a C# file, inactive regions included, with its 1-based line.</summary>
    public static IReadOnlyList<SourceLine> CSharpIdentifiers(string text)
    {
        var identifiers = new List<SourceLine>();
        ReadCSharp(text, 0, comments: null, identifiers);
        return identifiers;
    }

    private static void ReadCSharp(string text, int lineOffset, List<SourceLine>? comments, List<SourceLine>? identifiers)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        identifiers?.AddRange(root.DescendantTokens()
            .Where(static token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(token => new SourceLine(lineOffset + LineOf(token.GetLocation()), token.ValueText)));
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            var line = lineOffset + LineOf(trivia.GetLocation());
            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                ReadCSharp(trivia.ToFullString(), line - 1, comments, identifiers);
            }
            else if (comments is not null && IsComment(trivia))
            {
                comments.AddRange(Lines(trivia.ToFullString(), line));
            }
        }
    }

    private static int LineOf(Location location) => location.GetLineSpan().StartLinePosition.Line + 1;

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    private static List<SourceLine> Xml(string text) =>
    [
        .. XDocument.Parse(text, LoadOptions.SetLineInfo).DescendantNodes().OfType<XComment>()
            .SelectMany(static comment => Lines(comment.Value, ((IXmlLineInfo)comment).LineNumber)),
    ];

    /// <summary>The <c>#</c> comment of each line: a <c>#</c> that opens the line or follows whitespace, outside single or double quotes.</summary>
    private static List<SourceLine> HashComments(string text)
    {
        var result = new List<SourceLine>();
        var lines = text.Split(LineFeed);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd(CarriageReturn);
            char? quote = null;
            for (var position = 0; position < line.Length; position++)
            {
                var character = line[position];
                if (quote is not null)
                {
                    quote = character == quote ? null : quote;
                }
                else if (character is '"' or '\'')
                {
                    quote = character;
                }
                else if (character == '#' && (position == 0 || char.IsWhiteSpace(line[position - 1])))
                {
                    result.Add(new SourceLine(index + 1, line[position..]));
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>The comments of a JavaScript or TypeScript file, found by a lexer that skips strings, template literals and regular expression literals.</summary>
    private static List<SourceLine> ScriptComments(string text)
    {
        var result = new List<SourceLine>();
        var line = 1;
        var index = 0;
        var previous = NoCharacter;
        while (index < text.Length)
        {
            var character = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : NoCharacter;
            int end;
            if (character == '/' && next == '/')
            {
                end = IndexOrLength(text, "\n", index);
                result.AddRange(Lines(text[index..end], line));
            }
            else if (character == '/' && next == '*')
            {
                end = Math.Min(IndexOrLength(text, "*/", index + 2) + 2, text.Length);
                result.AddRange(Lines(text[index..end], line));
            }
            else if (character is '"' or '\'' or '`' || (character == '/' && StartsRegex(previous)))
            {
                end = LiteralEnd(text, index, character);
                previous = 'a';
            }
            else
            {
                end = index + 1;
                previous = char.IsWhiteSpace(character) ? previous : character;
            }

            line += text.AsSpan(index, end - index).Count(LineFeed);
            index = end;
        }

        return result;
    }

    private static int IndexOrLength(string text, string value, int start)
    {
        var found = text.IndexOf(value, start, StringComparison.Ordinal);
        return found < 0 ? text.Length : found;
    }

    /// <summary>A <c>/</c> starts a regular expression literal, not a division, after an operator, an opening bracket or the start of the file.</summary>
    private static bool StartsRegex(char previous) =>
        previous is NoCharacter or '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?' or '{' or '}' or ';' or '+' or '-' or '*' or '%' or '<' or '>' or '~' or '^';

    /// <summary>
    /// The index after the literal that opens at <paramref name="start"/>, honouring escapes and,
    /// for a regular expression, character classes. A string or regular expression ends at the
    /// line's end at the latest; a template literal may span lines.
    /// </summary>
    private static int LiteralEnd(string text, int start, char delimiter)
    {
        var inClass = false;
        var index = start + 1;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == Backslash)
            {
                index += 2;
                continue;
            }

            if (character == LineFeed && delimiter != '`')
            {
                return index;
            }

            if (delimiter == '/' && character is '[' or ']')
            {
                inClass = character == '[';
            }
            else if (character == delimiter && !inClass)
            {
                return index + 1;
            }

            index++;
        }

        return text.Length;
    }

    private static IEnumerable<SourceLine> Blocks(string text, Regex block) =>
        block.Matches(text).SelectMany(match => Lines(match.Value, text.AsSpan(0, match.Index).Count(LineFeed) + 1));

    /// <summary>A text's lines, numbered from <paramref name="firstLine"/>.</summary>
    private static IEnumerable<SourceLine> Lines(string text, int firstLine) =>
        text.Split(LineFeed).Select((part, offset) => new SourceLine(firstLine + offset, part.TrimEnd(CarriageReturn)));

    private static bool IsXml(string path) => XmlExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal));

    private static bool IsHash(string path) =>
        HashExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal))
        || HashFileNames.Any(name => path.EndsWith("/" + name, StringComparison.Ordinal) || string.Equals(path, name, StringComparison.Ordinal));

    private static bool IsScript(string path) => ScriptExtensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal));

    [GeneratedRegex(@"<#.*?#>", RegexOptions.Singleline, 1000)]
    private static partial Regex PowerShellBlock();
}
