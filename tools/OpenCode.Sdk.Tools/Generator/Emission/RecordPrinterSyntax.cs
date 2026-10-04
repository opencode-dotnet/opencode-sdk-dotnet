using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// The syntax a generated record's own ToString is built from. A record that must mask a value
/// cannot keep the compiler-synthesized ToString, so it overrides it and hands its members to the
/// SDK's runtime printers; every emitter that does so shares these shapes, so the printed forms
/// stay alike.
/// </summary>
internal static class RecordPrinterSyntax
{
    /// <summary><c>public override string ToString() =&gt; &lt;body&gt;;</c> under its summary.</summary>
    /// <param name="body">The expression the override returns.</param>
    /// <param name="summary">The override's XML summary.</param>
    /// <returns>The override declaration.</returns>
    public static MethodDeclarationSyntax ToStringOverride(ExpressionSyntax body, string summary) =>
        SyntaxFactory
            .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)), "ToString")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList())
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(body))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation(summary));

    /// <summary><c>RecordPrinter.Format(nameof(&lt;typeName&gt;), &lt;members&gt;)</c>.</summary>
    /// <param name="typeName">The printed record's type name.</param>
    /// <param name="members">The printed members in declaration order, from <see cref="Member"/>.</param>
    /// <returns>The format call.</returns>
    public static InvocationExpressionSyntax Format(string typeName, IEnumerable<ArgumentSyntax> members) =>
        EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("RecordPrinter"), "Format"),
            [SyntaxFactory.Argument(NameOf(typeName)), .. members]);

    /// <summary><c>("Name", Name)</c>, or <c>("Name", &lt;value&gt;)</c> when the printed value is not the member itself.</summary>
    /// <param name="memberName">The member's name.</param>
    /// <param name="value">The printed value; null prints the member.</param>
    /// <returns>The member's tuple argument.</returns>
    public static ArgumentSyntax Member(string memberName, ExpressionSyntax? value = null) =>
        SyntaxFactory.Argument(SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(
        [
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(memberName))),
            SyntaxFactory.Argument(value ?? SyntaxFactory.IdentifierName(memberName)),
        ])));

    /// <summary><c>RecordPrinter.Redact(&lt;value&gt;)</c>: the marker for a present value, nothing for an absent one.</summary>
    /// <param name="value">The masked value.</param>
    /// <returns>The redact call.</returns>
    public static InvocationExpressionSyntax Redact(ExpressionSyntax value) => EmissionSyntax.Invocation(
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("RecordPrinter"), "Redact"),
        SyntaxFactory.Argument(value));

    /// <summary><c>RecordPrinter.Redacted</c>, the marker itself.</summary>
    /// <returns>The marker access.</returns>
    public static MemberAccessExpressionSyntax Redacted() =>
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("RecordPrinter"), "Redacted");

    /// <summary><c>nameof(&lt;typeName&gt;)</c>.</summary>
    /// <param name="typeName">The named type.</param>
    /// <returns>The nameof expression.</returns>
    public static InvocationExpressionSyntax NameOf(string typeName) => EmissionSyntax.Invocation(
        SyntaxFactory.IdentifierName("nameof"),
        SyntaxFactory.Argument(SyntaxFactory.IdentifierName(typeName)));
}
