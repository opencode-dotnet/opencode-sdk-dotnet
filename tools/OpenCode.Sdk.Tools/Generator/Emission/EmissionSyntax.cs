using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

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

    public static LocalDeclarationStatementSyntax Local(string name, ExpressionSyntax value) =>
        SyntaxFactory.LocalDeclarationStatement(SyntaxFactory
            .VariableDeclaration(SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(name).WithInitializer(SyntaxFactory.EqualsValueClause(value)))));

    public static ObjectCreationExpressionSyntax JsonException(ExpressionSyntax message) => SyntaxFactory
        .ObjectCreationExpression(SyntaxFactory.IdentifierName("JsonException"))
        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(message))));

    public static ObjectCreationExpressionSyntax JsonException(string message) => JsonException(StringLiteral(message));

    public static LiteralExpressionSyntax StringLiteral(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));

    public static AccessorListSyntax GetOnlyAccessors() => SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))));

    public static LiteralExpressionSyntax MarkerLiteral(LiteralKind kind, string value) => kind switch
    {
        LiteralKind.String => StringLiteral(value),
        LiteralKind.Boolean when bool.TryParse(value, out var boolean) => SyntaxFactory.LiteralExpression(
            boolean ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
        LiteralKind.Number => throw new InvalidOperationException("Number marker literals have no emission consumer."),
        _ => throw new InvalidOperationException($"Marker value '{value}' is invalid for '{kind}'."),
    };
}
