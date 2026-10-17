using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class EmissionSyntax
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly SyntaxTrivia LineFeed = SyntaxFactory.EndOfLine("\n");

    public static GeneratedSource CreateSource(string relativePath, CompilationUnitSyntax compilationUnit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(compilationUnit);

        var normalized = compilationUnit.NormalizeWhitespace(indentation: "    ", eol: "\n", elasticTrivia: false).ToFullString();
        if (normalized.Length is 0 || normalized[^1] is not '\n')
        {
            normalized = $"{normalized}\n";
        }

        return new GeneratedSource
        {
            RelativePath = relativePath,
            Utf8Source = Utf8.GetBytes(normalized),
        };
    }

    public static CompilationUnitSyntax CompilationUnit(string namespaceName, IReadOnlyList<string> usingNames,
        IReadOnlyList<MemberDeclarationSyntax> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentNullException.ThrowIfNull(usingNames);
        ArgumentNullException.ThrowIfNull(members);

        var namespaceDeclaration = SyntaxFactory.FileScopedNamespaceDeclaration(QualifiedName(namespaceName))
            .WithMembers(SyntaxFactory.List(members));
        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usingNames
                .Distinct(StringComparer.Ordinal)
                .Where(name => !IsEnclosingNamespace(name, namespaceName))
                .OrderBy(static name => name is "System" || name.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(static name => name, StringComparer.Ordinal)
                .Select(static name => SyntaxFactory.UsingDirective(QualifiedName(name)))))
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceDeclaration));

        return compilationUnit.WithLeadingTrivia(
            SyntaxFactory.Comment(GenerationProvenance.FirstLine),
            LineFeed,
            SyntaxFactory.Comment(GenerationProvenance.SecondLine),
            LineFeed,
            LineFeed);
    }

    /// <summary>
    /// A file's own namespace and the namespaces enclosing it are in scope without a directive, so
    /// emitters can name a type's namespace without knowing which file declares the reference.
    /// </summary>
    private static bool IsEnclosingNamespace(string usingName, string namespaceName) =>
        string.Equals(usingName, namespaceName, StringComparison.Ordinal)
        || namespaceName.StartsWith($"{usingName}.", StringComparison.Ordinal);

    public static NameSyntax QualifiedName(string dottedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedName);
        var segments = dottedName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        NameSyntax result = SyntaxFactory.IdentifierName(segments[0]);
        for (var index = 1; index < segments.Length; index++)
        {
            result = SyntaxFactory.QualifiedName(result, SyntaxFactory.IdentifierName(segments[index]));
        }

        return result;
    }

    public static SyntaxTriviaList Documentation(string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        var normalized = NormalizeDocumentation(summary);
        var text = $"/// <summary>\n/// {EscapeXml(normalized)}\n/// </summary>\n";
        return SyntaxFactory.ParseLeadingTrivia(text);
    }

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
                ? property.WithLeadingTrivia(SyntaxFactory.Comment($"/// <inheritdoc cref=\"{EscapeXml(owner)}\" />"), LineFeed)
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

    public static AttributeListSyntax Attribute(string name, params AttributeArgumentSyntax[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        var attribute = SyntaxFactory.Attribute(QualifiedName(name));
        if (arguments.Length > 0)
        {
            attribute = attribute.WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(arguments)));
        }

        return SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute));
    }

    public static AttributeArgumentSyntax StringArgument(string value) =>
        SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(value ?? throw new ArgumentNullException(nameof(value)))));

    public static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax expression, string memberName) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            expression ?? throw new ArgumentNullException(nameof(expression)),
            SyntaxFactory.IdentifierName(memberName));

    public static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax expression, SimpleNameSyntax memberName) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            expression ?? throw new ArgumentNullException(nameof(expression)),
            memberName ?? throw new ArgumentNullException(nameof(memberName)));

    public static InvocationExpressionSyntax Invocation(ExpressionSyntax expression, params ArgumentSyntax[] arguments) =>
        SyntaxFactory.InvocationExpression(expression ?? throw new ArgumentNullException(nameof(expression)))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

    public static IReadOnlyList<StatementSyntax> ArgumentNullGuard(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        var parameter = SyntaxFactory.IdentifierName(parameterName);
        var guard = SyntaxFactory.ExpressionStatement(Invocation(
                MemberAccess(SyntaxFactory.IdentifierName("ArgumentNullException"), "ThrowIfNull"),
                SyntaxFactory.Argument(parameter)));
        return Array.AsReadOnly<StatementSyntax>([guard]);
    }

    public static IReadOnlyList<StatementSyntax> ArgumentNullOrEmptyGuard(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        var parameter = SyntaxFactory.IdentifierName(parameterName);
        var guard = SyntaxFactory.ExpressionStatement(Invocation(
                MemberAccess(SyntaxFactory.IdentifierName("ArgumentException"), "ThrowIfNullOrEmpty"),
                SyntaxFactory.Argument(parameter)));
        return Array.AsReadOnly<StatementSyntax>([guard]);
    }

    public static IReadOnlyList<StatementSyntax> ArgumentNullOrWhiteSpaceGuard(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        var parameter = SyntaxFactory.IdentifierName(parameterName);
        var guard = SyntaxFactory.ExpressionStatement(Invocation(
            MemberAccess(SyntaxFactory.IdentifierName("ArgumentException"), "ThrowIfNullOrWhiteSpace"),
            SyntaxFactory.Argument(parameter)));
        return Array.AsReadOnly<StatementSyntax>([guard]);
    }

    /// <summary>
    /// The guard pair every route value passes — non-whitespace and never a dot segment —
    /// emitted identically wherever a route value enters, so acceptance at one boundary
    /// can never outrun refusal at another.
    /// </summary>
    public static IReadOnlyList<StatementSyntax> RouteValueGuard(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        var parameter = SyntaxFactory.IdentifierName(parameterName);
        var whitespaceGuard = SyntaxFactory.ExpressionStatement(Invocation(
            MemberAccess(SyntaxFactory.IdentifierName("ArgumentException"), "ThrowIfNullOrWhiteSpace"),
            SyntaxFactory.Argument(parameter)));
        var dotSegmentGuard = SyntaxFactory.IfStatement(
            SyntaxFactory.IsPatternExpression(
                parameter,
                SyntaxFactory.BinaryPattern(
                    SyntaxKind.OrPattern,
                    SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal("."))),
                    SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(".."))))),
            SyntaxFactory.Block(ThrowArgumentException("Route values must not be dot segments.", parameterName)));
        return Array.AsReadOnly<StatementSyntax>([whitespaceGuard, dotSegmentGuard]);
    }

    /// <summary><c>throw new ArgumentException("message", nameof(parameterName));</c>, the refusal every emitted argument guard throws.</summary>
    public static ThrowStatementSyntax ThrowArgumentException(string message, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        return SyntaxFactory.ThrowStatement(SyntaxFactory
            .ObjectCreationExpression(SyntaxFactory.IdentifierName("ArgumentException"))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(message))),
                SyntaxFactory.Argument(Invocation(
                    SyntaxFactory.IdentifierName("nameof"),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameterName)))),
            ]))));
    }

    /// <summary>
    /// <c>subject.StartsWith("prefix", StringComparison.Ordinal)</c>: the one test a prefix-tagged
    /// arm's claim takes, emitted identically where the union converter dispatches to the arm,
    /// where the unknown carrier refuses a marker the arm claims, and where the arm's own marker
    /// refuses a value it does not claim, so the three can never disagree on the claim.
    /// </summary>
    public static InvocationExpressionSyntax StartsWithOrdinal(ExpressionSyntax subject, string prefix)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return Invocation(
            MemberAccess(subject, "StartsWith"),
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(prefix))),
            SyntaxFactory.Argument(MemberAccess(SyntaxFactory.IdentifierName("StringComparison"), "Ordinal")));
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
