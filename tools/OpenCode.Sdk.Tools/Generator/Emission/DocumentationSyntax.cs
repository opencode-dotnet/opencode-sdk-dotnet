using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class DocumentationSyntax
{
    public static SyntaxTriviaList Documentation(string summary) => MemberDocumentation(summary, []);

    public static SyntaxTriviaList Documentation(string summary, string? remarks) =>
        MemberDocumentation(summary, [], remarks: ProseRemarks(remarks));

    /// <summary>Applies bound text to a type and its directly declared properties, without interpreting wire roles.</summary>
    public static T DocumentedType<T>(T declaration, TypeDocumentationPlan documentation,
        IReadOnlyList<DocumentationRun>? existingRemarks = null,
        IReadOnlyDictionary<string, string>? inheritedMembers = null) where T : TypeDeclarationSyntax
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(documentation);

        var members = declaration.Members.Select(member =>
        {
            if (member is not PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: null } property)
            {
                return member;
            }

            if (documentation.Members.TryGetValue(property.Identifier.ValueText, out var text))
            {
                return property.WithLeadingTrivia(MemberDocumentation(text.Summary, [], remarks: ProseRemarks(text.Remarks)));
            }

            return inheritedMembers?.TryGetValue(property.Identifier.ValueText, out var owner) is true
                ? property.WithLeadingTrivia(SyntaxFactory.Comment($"/// <inheritdoc cref=\"{EscapeXml(owner)}\" />"), SyntaxFactory.EndOfLine("\n"))
                : member;
        });
        var result = (T)declaration.WithMembers(SyntaxFactory.List(members));
        if (documentation.Summary is { } summary)
        {
            var remarks = existingRemarks ?? [];
            if (!string.IsNullOrWhiteSpace(documentation.Remarks))
            {
                remarks = remarks.Count > 0
                    ? [.. remarks, DocumentationRun.Prose(" "), .. ProseRemarks(documentation.Remarks)]
                    : ProseRemarks(documentation.Remarks);
            }

            result = result.WithLeadingTrivia(MemberDocumentation(summary, [], remarks: remarks));
        }

        return result;
    }

    private static IReadOnlyList<DocumentationRun> ProseRemarks(string? remarks) =>
        string.IsNullOrWhiteSpace(remarks) ? [] : [DocumentationRun.Prose(NormalizeDocumentation(remarks))];

    public static SyntaxTriviaList MemberDocumentation(string summary, IReadOnlyList<DocumentedParameter> parameters,
        string? returns = null, IReadOnlyList<DocumentedException>? exceptions = null,
        IReadOnlyList<DocumentationRun>? remarks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(parameters);

        var text = new StringBuilder()
            .Append("/// <summary>\n/// ").Append(EscapeXml(NormalizeDocumentation(summary))).Append("\n/// </summary>\n");
        foreach (var parameter in parameters)
        {
            _ = text.Append("/// <param name=\"").Append(parameter.Name).Append("\">")
                .Append(EscapeXml(NormalizeDocumentation(parameter.Text))).Append("</param>\n");
        }

        if (returns is not null)
        {
            _ = text.Append("/// <returns>").Append(EscapeXml(NormalizeDocumentation(returns))).Append("</returns>\n");
        }

        foreach (var exception in exceptions ?? [])
        {
            _ = text.Append("/// <exception cref=\"").Append(exception.TypeName).Append("\">")
                .Append(EscapeXml(NormalizeDocumentation(exception.Text))).Append("</exception>\n");
        }

        if (remarks is { Count: > 0 })
        {
            _ = text.Append("/// <remarks>\n/// ");
            foreach (var run in remarks)
            {
                _ = run.IsCode
                    ? text.Append("<c>").Append(EscapeXml(run.Text)).Append("</c>")
                    : text.Append(EscapeXml(run.Text));
            }

            _ = text.Append("\n/// </remarks>\n");
        }

        return SyntaxFactory.ParseLeadingTrivia(text.ToString());
    }

    private static string NormalizeDocumentation(string value)
    {
        var result = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace && result.Length > 0)
                {
                    _ = result.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            _ = result.Append(character);
            previousWasWhitespace = false;
        }

        return result.ToString().Trim();
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);
}
