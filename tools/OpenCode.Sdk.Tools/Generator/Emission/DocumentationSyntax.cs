using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class DocumentationSyntax
{
    public static string DocumentationSummary(OperationPlan operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.CuratedSummary is { } curated)
        {
            return curated;
        }

        if (operation.Summary is null)
        {
            return operation.Description ?? $"Calls the '{operation.RouteTemplate}' operation.";
        }

        if (operation.Description is null)
        {
            return operation.Summary;
        }

        var separator = operation.Summary.Length > 0 && operation.Summary[^1] is '.' ? " " : ". ";
        return string.Concat(operation.Summary, separator, operation.Description);
    }

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
                return property.WithLeadingTrivia(MemberDocumentation(text.Summary, [], remarks: ProseRemarks(text.Remarks, curated: true), summaryIsCurated: true));
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
                var typeRemarks = ProseRemarks(documentation.Remarks);
                if (documentation.CuratedRemarks is { } curated)
                {
                    var prefix = NormalizeDocumentation(documentation.Remarks)[..^NormalizeDocumentation(curated).Length];
                    typeRemarks = [DocumentationRun.Prose(prefix), .. ProseRemarks(curated, curated: true)];
                }

                remarks = remarks.Count > 0
                    ? [.. remarks, DocumentationRun.Prose(" "), .. typeRemarks]
                    : typeRemarks;
            }

            result = result.WithLeadingTrivia(MemberDocumentation(summary, [], remarks: remarks, summaryIsCurated: documentation.SummaryIsCurated));
        }

        return result;
    }

    public static IReadOnlyList<DocumentationRun> ProseRemarks(string? remarks, bool curated = false)
    {
        if (string.IsNullOrWhiteSpace(remarks))
        {
            return [];
        }

        var text = NormalizeDocumentation(remarks);
        var runs = new List<DocumentationRun>();
        var start = 0;
        for (var open = curated ? text.IndexOf('`', StringComparison.Ordinal) : -1; open >= 0; open = text.IndexOf('`', start, StringComparison.Ordinal))
        {
            var close = text.IndexOf('`', open + 1, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            runs.Add(DocumentationRun.Prose(text[start..open]));
            runs.Add(DocumentationRun.Code(text[(open + 1)..close]));
            start = close + 1;
        }

        runs.Add(DocumentationRun.Prose(text[start..]));
        return runs;
    }

    public static SyntaxTriviaList MemberDocumentation(string summary, IReadOnlyList<DocumentedParameter> parameters,
        string? returns = null, IReadOnlyList<DocumentedException>? exceptions = null,
        IReadOnlyList<DocumentationRun>? remarks = null, bool summaryIsCurated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(parameters);

        var text = new StringBuilder()
            .Append("/// <summary>\n/// ").Append(string.Concat(ProseRemarks(summary, summaryIsCurated).Select(RenderRun))).Append("\n/// </summary>\n");
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
                _ = text.Append(RenderRun(run));
            }

            _ = text.Append("\n/// </remarks>\n");
        }

        return SyntaxFactory.ParseLeadingTrivia(text.ToString());
    }

    private static string RenderRun(DocumentationRun run) => run switch
    {
        { Cref: { } cref } => "<see cref=\"" + EscapeXml(cref) + "\" />",
        { IsCode: true } => "<c>" + EscapeXml(run.Text) + "</c>",
        _ => EscapeXml(run.Text),
    };

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
