using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class ModelPropertyEmitter
{
    /// <summary>
    /// A discriminator is emitted under the marker member its union promises rather than under
    /// the property's own derived name, so a variant whose wire property derives another name
    /// still satisfies the one interface member. The wire name it serializes as is untouched.
    /// A literal tag is a constant; the prefix-tagged arm's marker is a required string whose
    /// initializer refuses any value outside the prefix.
    /// </summary>
    internal static PropertyDeclarationSyntax EmitProperty(ModelPropertyPlan property, ChainMarkerPlan? marker)
    {
        var memberName = marker?.MemberName ?? property.Name;
        var propertyType = property.EmitsOptionalWrapper
            ? TypeSyntaxEmitter.Generic("Optional", TypeSyntaxEmitter.Emit(property.Type))
            : TypeSyntaxEmitter.Emit(property.Type);
        var declaration = SyntaxFactory
            .PropertyDeclaration(propertyType, memberName)
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(property.WireName)))
            .WithLeadingTrivia(EmissionSyntax.Documentation(property.Description ?? GeneratedDisplayName.MemberSummary(memberName)));
        if (!property.EmitsOptionalWrapper && ContainsSpecialNumber(property.Type))
        {
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonNumberHandling",
                SyntaxFactory.AttributeArgument(EmissionSyntax.MemberAccess(
                    SyntaxFactory.IdentifierName("JsonNumberHandling"),
                    "AllowNamedFloatingPointLiterals"))));
        }

        if (!property.IsRequired)
        {
            // The wrapper's absent state is default, and WhenWritingNull is invalid on a
            // non-nullable value type: omission is decided by the struct, not by CLR null.
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonIgnore",
                SyntaxFactory
                    .AttributeArgument(EmissionSyntax.MemberAccess(
                        SyntaxFactory.IdentifierName("JsonIgnoreCondition"),
                        property.EmitsOptionalWrapper ? "WhenWritingDefault" : "WhenWritingNull"))
                    .WithNameEquals(SyntaxFactory.NameEquals("Condition"))));
        }

        if (property.EmitsOptionalWrapper)
        {
            // Member-level rather than type-level: the attribute argument cannot name an
            // unbound generic, so each instantiation points at its own closed converter.
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonConverter",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(
                    TypeSyntaxEmitter.EmitNamed(OptionalConverterNamePolicy.ConverterTypeName(property.Type))))));
        }

        if (marker?.Prefix is { } prefix)
        {
            if (!property.IsRequired)
            {
                throw new InvalidOperationException($"Property '{property.Name}' carries a prefix-tagged marker but is not required.");
            }

            return declaration
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.RequiredKeyword)))
                .WithAccessorList(EmitPrefixGuardedAccessors(property.WireName, prefix));
        }

        if (marker is not null)
        {
            return declaration
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmitLiteral(property)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
        };
        if (property.IsRequired)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.RequiredKeyword));
        }

        declaration = declaration
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithAccessorList(EmitAutoAccessors());
        return declaration;
    }

    private static AccessorListSyntax EmitAutoAccessors() => SyntaxFactory.AccessorList(SyntaxFactory.List(
    [
        SyntaxFactory
            .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
        SyntaxFactory
            .AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
    ]));

    /// <summary>
    /// <c>get; init { … }</c> for the prefix-tagged arm's marker. The initializer refuses null
    /// and any value outside the prefix — the typed twin of the unknown carrier refusing a
    /// marker the prefix claims — and stores through the <c>field</c> keyword.
    /// </summary>
    private static AccessorListSyntax EmitPrefixGuardedAccessors(string wireName, string prefix)
    {
        var refusal = SyntaxFactory.IfStatement(
            SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                EmissionSyntax.StartsWithOrdinal(SyntaxFactory.IdentifierName("value"), prefix)),
            SyntaxFactory.Block(EmissionSyntax.ThrowArgumentException(
                $"The '{wireName}' marker must carry the '{prefix}' prefix.",
                "value")));
        var store = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.FieldExpression(),
            SyntaxFactory.IdentifierName("value")));
        return SyntaxFactory.AccessorList(SyntaxFactory.List(
        [
            SyntaxFactory
                .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory
                .AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(SyntaxFactory.List(
                [
                    .. EmissionSyntax.ArgumentNullGuard("value"),
                    refusal,
                    store,
                ]))),
        ]));
    }

    private static LiteralExpressionSyntax EmitLiteral(ModelPropertyPlan property) => property.LiteralKind switch
    {
        LiteralKind.String => SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(property.LiteralValue ?? throw new InvalidOperationException("String literal had no value."))),
        LiteralKind.Boolean when bool.TryParse(property.LiteralValue, out var value) => SyntaxFactory.LiteralExpression(
            value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
        LiteralKind.Number => throw new InvalidOperationException(
            $"Property '{property.Name}' uses a number literal, which has no emission consumer."),
        _ => throw new InvalidOperationException($"Property '{property.Name}' has an invalid literal plan."),
    };

    private static bool ContainsSpecialNumber(TypeReferencePlan type) => type switch
    {
        SpecialNumberTypeReferencePlan => true,
        ListTypeReferencePlan list => ContainsSpecialNumber(list.ElementType),
        DictionaryTypeReferencePlan dictionary => ContainsSpecialNumber(dictionary.ValueType),
        _ => false,
    };
}
