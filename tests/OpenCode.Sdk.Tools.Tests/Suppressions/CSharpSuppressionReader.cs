using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Reads the suppressions one C# file declares: every <c>#pragma warning disable</c>, including
/// those in a region the parse leaves inactive; every <c>[SuppressMessage]</c>,
/// <c>[UnconditionalSuppressMessage]</c> and <c>[SlopwatchSuppress]</c> attribute; every
/// slopwatch-ignore comment; and a file-based app's <c>#:property</c> directives that set a warning
/// list or switch the analyzer wall. A pragma without IDs counts as rule <c>*</c>. An attribute in an
/// inactive <c>#if</c> region is not read.
/// </summary>
internal static partial class CSharpSuppressionReader
{
    private const string EveryRule = "*";

    public static IReadOnlyList<Suppression> Read(string origin, string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        var result = new List<Suppression>();
        foreach (var pragma in root.DescendantNodes(descendIntoTrivia: true).OfType<PragmaWarningDirectiveTriviaSyntax>()
            .Where(static pragma => pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)))
        {
            AddPragma(result, origin, pragma.ErrorCodes.Select(static code => code.ToString()));
        }

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            {
                foreach (Match match in InactivePragma().Matches(trivia.ToString()))
                {
                    AddPragma(result, origin, match.Groups["codes"].Value.Split("//", 2)[0].Split(','));
                }
            }
            else if (IsComment(trivia))
            {
                result.AddRange(SlopwatchIgnores(origin, trivia.ToString()));
            }
        }

        result.AddRange(root.DescendantNodes().OfType<AttributeSyntax>().Select(attribute => Read(origin, attribute)).OfType<Suppression>());
        result.AddRange(PropertyDirective().Matches(text)
            .SelectMany(match => ProjectSuppressionReader.Property(origin, match.Groups["name"].Value, match.Groups["value"].Value)));
        return result;
    }

    /// <summary>Every slopwatch-ignore marker in one comment, by the slopwatch rule it names.</summary>
    internal static IEnumerable<Suppression> SlopwatchIgnores(string origin, string comment) =>
        SlopwatchIgnore().Matches(comment)
            .Select(match => new Suppression(SuppressionChannel.SlopwatchIgnore, origin, string.Empty, match.Groups["rule"].Value, string.Empty));

    private static Suppression? Read(string origin, AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => string.Empty,
        };
        if (name.EndsWith(nameof(Attribute), StringComparison.Ordinal))
        {
            name = name[..^nameof(Attribute).Length];
        }

        return name switch
        {
            "SuppressMessage" or "UnconditionalSuppressMessage" => new Suppression(
                SuppressionChannel.SuppressMessage, origin, string.Empty, CheckId(origin, attribute), string.Empty),
            "SlopwatchSuppress" => new Suppression(
                SuppressionChannel.SlopwatchSuppress, origin, string.Empty, PositionalText(origin, attribute, 0), string.Empty),
            _ => null,
        };
    }

    private static string CheckId(string origin, AttributeSyntax attribute)
    {
        var checkId = PositionalText(origin, attribute, 1);
        var separator = checkId.IndexOf(':', StringComparison.Ordinal);
        return (separator < 0 ? checkId : checkId[..separator]).Trim();
    }

    private static string PositionalText(string origin, AttributeSyntax attribute, int position)
    {
        var argument = attribute.ArgumentList?.Arguments
            .Where(static argument => argument.NameEquals is null && argument.NameColon is null)
            .ElementAtOrDefault(position);
        return argument?.Expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            { } expression => expression.ToString(),
            null => throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{origin}: [{attribute.Name}] at line {attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1} names no rule in argument {position + 1}.")),
        };
    }

    private static void AddPragma(List<Suppression> result, string origin, IEnumerable<string> codes)
    {
        var rules = codes
            .Select(static code => code.Trim())
            .Where(static code => code.Length > 0)
            .DefaultIfEmpty(EveryRule);
        result.AddRange(rules.Select(rule => new Suppression(SuppressionChannel.Pragma, origin, string.Empty, rule, string.Empty)));
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
        || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
        || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    [GeneratedRegex(@"^[ \t]*#[ \t]*pragma[ \t]+warning[ \t]+disable\b(?<codes>[^\r\n]*)", RegexOptions.Multiline, 1000)]
    private static partial Regex InactivePragma();

    [GeneratedRegex(@"slopwatch-ignore:?\s*(?<rule>SW\d{3})", RegexOptions.None, 1000)]
    private static partial Regex SlopwatchIgnore();

    [GeneratedRegex(@"^#:property[ \t]+(?<name>[A-Za-z]+)[ \t]*=(?<value>[^\r\n]*)", RegexOptions.Multiline, 1000)]
    private static partial Regex PropertyDirective();
}
