using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>Emits the property's conversion, including the null handling a direct converter bypasses.</summary>
internal static class RecordMemberReaderEmitter
{
    internal static TypeSyntax PropertyType(ModelPropertyPlan property) => property.EmitsOptionalWrapper
        ? TypeSyntaxEmitter.Generic("Optional", TypeSyntaxEmitter.Emit(property.Type)) : TypeSyntaxEmitter.Emit(property.Type);

    internal static IEnumerable<FieldDeclarationSyntax> Fields(IReadOnlyList<ModelPropertyPlan> properties) => properties
        .Where(static property => property.EmitsOptionalWrapper)
        .DistinctBy(static property => ConverterNamePolicy.Optional(property.Type), StringComparer.Ordinal)
        .Select(static property => SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName(ConverterNamePolicy.Optional(property.Type)),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(ConverterNamePolicy.Optional(property.Type))
                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.IdentifierName(ConverterNamePolicy.Optional(property.Type))).WithArgumentList(SyntaxFactory.ArgumentList()))))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword))));

    internal static IReadOnlyList<StatementSyntax> Assignment(ModelPropertyPlan property, string local)
    {
        var read = Value(property);
        var nullToken = SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType"),
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), "Null"));
        var statements = new List<StatementSyntax>();
        // Binary converters read null in band even though the bound binary shape retains ClrNull.
        if (!property.EmitsOptionalWrapper && property.Type is not BinaryTypeReferencePlan
            && property.Type.JsonNullRepresentation is JsonNullRepresentation.ClrNull)
        {
            if (property.Type.IsNullable)
            {
                read = SyntaxFactory.ConditionalExpression(nullToken, SyntaxFactory.DefaultExpression(PropertyType(property)), read);
            }
            else
            {
                statements.Add(SyntaxFactory.IfStatement(nullToken, SyntaxFactory.Block(ConverterSyntax.ThrowJson("A non-nullable member was null."))));
            }
        }

        statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(local), read)));
        return statements;
    }

    private static ExpressionSyntax Value(ModelPropertyPlan property)
    {
        if (property.EmitsOptionalWrapper)
        {
            return EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(
                    SyntaxFactory.IdentifierName(ConverterNamePolicy.Optional(property.Type)), "Read"),
                ReaderArgument(), SyntaxFactory.Argument(SyntaxFactory.TypeOfExpression(PropertyType(property))),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("options")));
        }

        if (property.Type is SpecialNumberTypeReferencePlan)
        {
            return SpecialNumber();
        }

        var method = property.Type is NamedTypeReferencePlan named ? named.Name switch
        {
            "string" => "GetString",
            "bool" => "GetBoolean",
            "long" => "GetInt64",
            "double" => "GetDouble",
            _ => null,
        } : null;
        if (method is not null)
        {
            return EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), method));
        }

        return EmissionSyntax.Invocation(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("UnionMemberReader"), TypeSyntaxEmitter.Generic(
                    ContainsSpecialNumber(property.Type) ? "ReadSpecialNumbers" : "Read", TypeSyntaxEmitter.Emit(property.Type))),
            ReaderArgument(), SyntaxFactory.Argument(SyntaxFactory.IdentifierName("options")));
    }

    private static ConditionalExpressionSyntax SpecialNumber()
    {
        var literals = new[] { (Wire: "NaN", Member: "NaN"), (Wire: "Infinity", Member: "PositiveInfinity"), (Wire: "-Infinity", Member: "NegativeInfinity") };
        var arms = literals.Select(static literal => SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(literal.Wire))),
                EmissionSyntax.MemberAccess(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.DoubleKeyword)), literal.Member)))
            .Append(SyntaxFactory.SwitchExpressionArm(SyntaxFactory.DiscardPattern(), SyntaxFactory.ThrowExpression(
                EmissionSyntax.JsonException("The JSON string is not a named floating-point literal."))));
        var named = SyntaxFactory.SwitchExpression(EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(
            SyntaxFactory.IdentifierName("reader"), "GetString"))).WithArms(SyntaxFactory.SeparatedList(arms));
        return SyntaxFactory.ConditionalExpression(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType"),
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), "String")), named,
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetDouble")));
    }

    private static ArgumentSyntax ReaderArgument() => SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader"))
        .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword));

    private static bool ContainsSpecialNumber(TypeReferencePlan type) => type switch
    {
        SpecialNumberTypeReferencePlan => true,
        ListTypeReferencePlan list => ContainsSpecialNumber(list.ElementType),
        DictionaryTypeReferencePlan dictionary => ContainsSpecialNumber(dictionary.ValueType),
        _ => false,
    };
}
