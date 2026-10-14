using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits the wire spelling of an enum-valued query member as a generated switch over the
/// same <see cref="EnumModelPlan"/> the model emitter renders, so the query string and the
/// JSON member name can never drift apart. The switch replaces reflection and
/// <c>Enum.ToString</c>, which neither trimming nor AOT can follow to the member-name
/// attribute.
/// </summary>
internal static class QueryEnumWireValueEmitter
{
    private const string MethodName = "ToWireValue";

    /// <summary>Names the generated converter every route builder calls for an enum query member.</summary>
    public static string ConverterName => MethodName;

    /// <summary>
    /// Emits one converter per named enum. The converters are private members of the routes
    /// root, so every family's nested container reaches them, and the routes emitter names each
    /// enum once so an enum two families share is converted in exactly one place.
    /// </summary>
    public static IReadOnlyList<MemberDeclarationSyntax> Emit(IReadOnlyList<string> enumTypeNames,
        IReadOnlyList<ModelPlan> models)
    {
        ArgumentNullException.ThrowIfNull(enumTypeNames);
        ArgumentNullException.ThrowIfNull(models);

        var enums = models.OfType<EnumModelPlan>().ToDictionary(static model => model.Name, StringComparer.Ordinal);
        return Array.AsReadOnly<MemberDeclarationSyntax>(
        [
            // Unreachable through the binder: QueryRequestFacetBinder refuses a query enum whose
            // model the type-name map does not carry, so an operation reaching emission always
            // has one. It stays a throw rather than a skip because the alternative is emitting a
            // converter set that silently omits one enum, which fails at run time in the caller's
            // process instead of here.
            .. enumTypeNames.Order(StringComparer.Ordinal).Select(typeName => EmitConverter(
                enums.TryGetValue(typeName, out var model)
                    ? model
                    : throw new InvalidOperationException($"Query enum '{typeName}' has no generated model."))),
        ]);
    }

    /// <summary>Gets every enum type name the operations' query members are typed with, once each, ordered.</summary>
    public static IEnumerable<string> EnumTypeNames(IReadOnlyList<OperationPlan> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        return operations
            .Where(static operation => operation.QueryRequest is not null)
            .SelectMany(static operation => operation.QueryRequest!.Properties)
            .Where(static property => property.Kind is QueryValueKind.Enum)
            .Select(static property => property.EnumTypeName
                                       ?? throw new InvalidOperationException(
                                           $"Query property '{property.PropertyName}' has no enum type name."))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
    }

    private static MethodDeclarationSyntax EmitConverter(EnumModelPlan model)
    {
        var type = TypeSyntaxEmitter.EmitNamed(model.Name);
        var value = SyntaxFactory.IdentifierName("value");
        var arms = new List<SwitchExpressionArmSyntax>(
        [
            .. model.Values.Select(member => SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.ConstantPattern(EmissionSyntax.MemberAccess(type, member.Name)),
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(member.WireValue)))),
            // An unset optional member contributes nothing to the query string.
            SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.DiscardPattern(),
                SyntaxFactory.ThrowExpression(SyntaxFactory
                    .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed("ArgumentOutOfRangeException"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    [
                        SyntaxFactory.Argument(EmissionSyntax.Invocation(
                            SyntaxFactory.IdentifierName("nameof"),
                            SyntaxFactory.Argument(value))),
                        SyntaxFactory.Argument(value),
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal($"Unknown {model.Name} value."))),
                    ]))))),
        ]);
        return SyntaxFactory
            .MethodDeclaration(
                SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
                MethodName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(SyntaxFactory.NullableType(type)))))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.SwitchExpression(
                value,
                SyntaxFactory.SeparatedList(arms))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                $"Spells one '{model.Name}' member the way the wire query expects it."));
    }
}
