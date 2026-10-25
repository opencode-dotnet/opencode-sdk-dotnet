using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits the <c>Classify</c> method that makes one adapter the single authority on what a
/// status means under its operation's pinned status table. Both adapter emitters share this
/// shape so one-shot and stream contracts classify identically: declared success first, the
/// undeclared 2xx range, each declared error status, then the tolerant undeclared-error arm.
/// </summary>
internal static class StatusVerdictEmitter
{
    public static MethodDeclarationSyntax EmitClassify(
        int successStatus,
        bool noContentSuccess,
        IEnumerable<int> declaredErrorStatuses,
        bool overrides)
    {
        ArgumentNullException.ThrowIfNull(declaredErrorStatuses);

        var arms = new List<SwitchExpressionArmSyntax>
        {
            SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.ConstantPattern(Number(successStatus)),
                Verdict(noContentSuccess ? "NoContentSuccess" : "Success")),
            SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.BinaryPattern(
                    SyntaxKind.AndPattern,
                    SyntaxFactory.RelationalPattern(SyntaxFactory.Token(SyntaxKind.GreaterThanEqualsToken), Number(200)),
                    SyntaxFactory.RelationalPattern(SyntaxFactory.Token(SyntaxKind.LessThanToken), Number(300))),
                Verdict("UndeclaredSuccess")),
        };
        arms.AddRange(declaredErrorStatuses.Select(static status => SyntaxFactory.SwitchExpressionArm(
            SyntaxFactory.ConstantPattern(Number(status)),
            Verdict("DeclaredError"))));
        arms.Add(SyntaxFactory.SwitchExpressionArm(SyntaxFactory.DiscardPattern(), Verdict("UndeclaredError")));

        var modifiers = overrides
            ? SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            : SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
        return SyntaxFactory.MethodDeclaration(TypeSyntaxEmitter.EmitNamed("StatusVerdict"), "Classify")
            .WithModifiers(modifiers)
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("status"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))))))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.SwitchExpression(
                SyntaxFactory.IdentifierName("status"),
                SyntaxFactory.SeparatedList(arms))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Classifies a status under this operation's pinned contract."));
    }

    private static MemberAccessExpressionSyntax Verdict(string name) =>
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StatusVerdict"), name);

    private static LiteralExpressionSyntax Number(int value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
}
