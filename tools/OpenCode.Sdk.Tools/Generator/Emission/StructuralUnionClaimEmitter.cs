using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class StructuralUnionClaimEmitter
{
    /// <summary>
    /// Emits the first-match read of the object arms that share the object token: the value is
    /// buffered once, each arm's claim is tried in declaration order, the first that holds
    /// deep-parses the value as that arm, and a value no claim holds stays raw as <c>Unknown</c>.
    /// </summary>
    internal static MethodDeclarationSyntax EmitReadObject(StructuralUnionModelPlan union)
    {
        var body = new List<StatementSyntax>
        {
            ConverterSyntax.PayloadDocument(),
            EmissionSyntax.Local("root", EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("document"), "RootElement")),
        };
        var unconditional = false;
        foreach (var arm in union.Arms.Where(static arm => arm.Claim is not null))
        {
            var select = SyntaxFactory.ReturnStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.Name), $"From{arm.Name}"),
                SyntaxFactory.Argument(EmissionSyntax.Invocation(
                    SyntaxFactory
                        .GenericName("ReadKnown")
                        .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(TypeSyntaxEmitter.Emit(arm.Type)))),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("root"))))));
            var conditions = ClaimConditions(arm.Claim!).ToArray();
            if (conditions.Length is 0)
            {
                // A claim with nothing to check takes every object; the binder refuses any arm
                // after it, so it is the last one tried.
                body.Add(select);
                unconditional = true;
                break;
            }

            var condition = conditions.Aggregate(static (left, right) =>
                SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, left, right));
            body.Add(SyntaxFactory.IfStatement(condition, SyntaxFactory.Block(select)));
        }

        if (!unconditional)
        {
            body.Add(SyntaxFactory.ReturnStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.Name), "FromUnknown"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("root")))));
        }

        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), "ReadObject")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("reader"))
                    .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader"))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))))))
            .WithBody(SyntaxFactory.Block(body));
    }

    /// <summary>
    /// A required key with a sentinel must hold one of its values, a required key without one must
    /// be present, and an optional sentinel must hold one of its values when it is present.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> ClaimConditions(StructuralObjectClaimPlan claim)
    {
        foreach (var requiredKey in claim.RequiredKeys)
        {
            var sentinel = claim.Sentinels.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Property, requiredKey));
            if (sentinel is null)
            {
                yield return ClaimCall("Has", requiredKey, []);
                continue;
            }

            yield return ClaimCall(sentinel.Kind is LiteralKind.Boolean ? "IsBoolean" : "IsText", requiredKey, SentinelValues(sentinel));
        }

        foreach (var sentinel in claim.Sentinels.Where(candidate => !claim.RequiredKeys.Contains(candidate.Property, StringComparer.Ordinal)))
        {
            yield return ClaimCall(sentinel.Kind is LiteralKind.Boolean ? "AllowsBoolean" : "AllowsText", sentinel.Property, SentinelValues(sentinel));
        }
    }

    private static InvocationExpressionSyntax ClaimCall(string method, string property, IEnumerable<ExpressionSyntax> values) =>
        EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StructuralUnionClaim"), method),
            [
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("root")),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(property))),
                .. values.Select(SyntaxFactory.Argument),
            ]);

    private static IEnumerable<ExpressionSyntax> SentinelValues(StructuralSentinelPlan sentinel) => sentinel.Kind is LiteralKind.Boolean
        ? sentinel.Values.Select(static value => (ExpressionSyntax)SyntaxFactory.LiteralExpression(
            StringComparer.Ordinal.Equals(value, "true") ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression))
        : sentinel.Values.Select(static value => (ExpressionSyntax)SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value)));
}
