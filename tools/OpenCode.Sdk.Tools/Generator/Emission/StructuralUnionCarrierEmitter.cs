using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class StructuralUnionCarrierEmitter
{
    internal static GeneratedSource EmitCarrier(StructuralUnionModelPlan union, string relativePath, GeneratedNamespacePlan namespaces)
    {
        var members = new List<MemberDeclarationSyntax>
        {
            EmitValueField(),
            EmitConstructor(union),
            EmitKindProperty(union),
        };
        foreach (var arm in union.Arms)
        {
            members.Add(EmitArmProperty(union, arm));
            members.Add(EmitFactory(union, arm));
        }

        members.Add(EmitUnknownProperty(union));
        members.Add(EmitUnknownFactory(union));
        members.Add(EmitValueGetter(union));
        members.Add(EmitToString(union));

        var declaration = SyntaxFactory
            .RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), union.Name)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(union.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
            .AddAttributeLists(EmissionSyntax.Attribute(
                "JsonConverter",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(
                    TypeSyntaxEmitter.EmitNamed($"{union.Name}JsonConverter")))))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                union.Description ?? $"Represents {GeneratedDisplayName.WithIndefiniteArticle(union.Name)} structural union."));
        declaration = DocumentationSyntax.DocumentedType(declaration, union.Documentation);
        var unit = EmissionSyntax.CompilationUnit(
            union.Namespace,
            [
                namespaces.Internal,
                "System",
                "System.Collections.Generic",
                "System.Text.Json",
                "System.Text.Json.Serialization",
            ],
            [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }

    /// <summary>
    /// A record's compiler-synthesized ToString prints every public property through PrintMembers,
    /// and an inactive arm throws by design. The union overrides ToString itself instead of
    /// declaring PrintMembers: a sealed record with no record base may only declare that hook
    /// private (CS8879), where IDE0051 cannot see the synthesized caller and the writer's format
    /// pass would strip it. The kind enum's member names are the arm names, so Kind.ToString()
    /// names the active arm for the printer.
    /// </summary>
    /// <remarks>
    /// When a known arm carries a secret member, a token this pin does not recognize may carry
    /// one too, so the Unknown arm prints the redaction marker in place of its preserved text.
    /// </remarks>
    private static MethodDeclarationSyntax EmitToString(StructuralUnionModelPlan union)
    {
        ExpressionSyntax value = SyntaxFactory.IdentifierName("_value");
        if (union.MasksUnknownPayload)
        {
            value = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.IsPatternExpression(
                    SyntaxFactory.IdentifierName("Kind"),
                    SyntaxFactory.ConstantPattern(KindMember(union, "Unknown"))),
                RecordPrinterSyntax.Redacted(),
                value);
        }

        return RecordPrinterSyntax.ToStringOverride(
            EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StructuralUnionPrinter"), "Format"),
                SyntaxFactory.Argument(RecordPrinterSyntax.NameOf(union.Name)),
                SyntaxFactory.Argument(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("Kind"), "ToString"))),
                SyntaxFactory.Argument(value)),
            union.MasksUnknownPayload
                ? "Prints the kind and the active arm, an unrecognized token masked; the inactive arms throw by design."
                : "Prints the kind and the active arm; the inactive arms throw by design.");
    }

    private static FieldDeclarationSyntax EmitValueField() => SyntaxFactory
        .FieldDeclaration(
            SyntaxFactory
                .VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("_value"))))
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
            SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static ConstructorDeclarationSyntax EmitConstructor(StructuralUnionModelPlan union) =>
        SyntaxFactory
            .ConstructorDeclaration(union.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("kind"))
                    .WithType(TypeSyntaxEmitter.EmitNamed(union.KindTypeName)),
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("value"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))),
            ])))
            .WithBody(SyntaxFactory.Block(
                Assign("Kind", "kind"),
                Assign("_value", "value")));

    private static ExpressionStatementSyntax Assign(string target, string value) => SyntaxFactory.ExpressionStatement(
        SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(target),
            SyntaxFactory.IdentifierName(value)));

    private static PropertyDeclarationSyntax EmitKindProperty(StructuralUnionModelPlan union) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitNamed(union.KindTypeName), "Kind")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(EmissionSyntax.GetOnlyAccessors())
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Gets the active structural union arm."));

    private static PropertyDeclarationSyntax EmitArmProperty(StructuralUnionModelPlan union, StructuralUnionArmPlan arm) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.Emit(arm.Type), arm.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.Invocation(
                SyntaxFactory
                    .GenericName("GetValue")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(TypeSyntaxEmitter.Emit(arm.Type)))),
                SyntaxFactory.Argument(KindMember(union, arm.Name)))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(GeneratedDisplayName.MemberSummary(arm.Name)));

    private static MethodDeclarationSyntax EmitFactory(StructuralUnionModelPlan union, StructuralUnionArmPlan arm)
    {
        var statements = new List<StatementSyntax>();
        if (IsReferenceType(arm.Type))
        {
            statements.AddRange(EmissionSyntax.ArgumentNullGuard("value"));
        }

        statements.Add(SyntaxFactory.ReturnStatement(NewCarrier(union, arm.Name, SyntaxFactory.IdentifierName("value"))));
        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), $"From{arm.Name}")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(TypeSyntaxEmitter.Emit(arm.Type)))))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.Documentation($"Creates {GeneratedDisplayName.WithIndefiniteArticle(union.Name)} from its {GeneratedDisplayName.Of(arm.Name)} arm."));
    }

    private static bool IsReferenceType(TypeReferencePlan type) => type switch
    {
        ListTypeReferencePlan or DictionaryTypeReferencePlan => true,
        NamedTypeReferencePlan { Name: "bool" or "double" or "long" } => false,
        SpecialNumberTypeReferencePlan => false,
        BinaryTypeReferencePlan => false,
        NamedTypeReferencePlan => true,
        _ => true,
    };

    private static PropertyDeclarationSyntax EmitUnknownProperty(StructuralUnionModelPlan union) =>
        SyntaxFactory
            .PropertyDeclaration(SyntaxFactory.IdentifierName("JsonElement"), "Unknown")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.Invocation(
                SyntaxFactory
                    .GenericName("GetValue")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName("JsonElement")))),
                SyntaxFactory.Argument(KindMember(union, "Unknown")))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Gets the preserved raw payload for an unrecognized token kind."));

    private static MethodDeclarationSyntax EmitUnknownFactory(StructuralUnionModelPlan union)
    {
        var valueKind = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "ValueKind");
        var undefined = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonValueKind"), "Undefined");
        var guard = SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, valueKind, undefined),
            SyntaxFactory.ThrowStatement(SyntaxFactory
                .ObjectCreationExpression(SyntaxFactory.IdentifierName("ArgumentException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                [
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal("The unknown value must be a parsed JSON element."))),
                    SyntaxFactory.Argument(EmissionSyntax.Invocation(
                        SyntaxFactory.IdentifierName("nameof"),
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("value")))),
                ])))));
        var clone = EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "Clone"));
        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), "FromUnknown")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(SyntaxFactory.IdentifierName("JsonElement")))))
            .WithBody(SyntaxFactory.Block(
                guard,
                SyntaxFactory.ReturnStatement(NewCarrier(union, "Unknown", clone))))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Creates a structural union value preserving an unrecognized JSON token."));
    }

    private static ObjectCreationExpressionSyntax NewCarrier(StructuralUnionModelPlan union, string armName, ExpressionSyntax value) =>
        SyntaxFactory
            .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(union.Name))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(KindMember(union, armName)),
                SyntaxFactory.Argument(value),
            ])));

    private static MethodDeclarationSyntax EmitValueGetter(StructuralUnionModelPlan union)
    {
        var mismatch = SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            SyntaxFactory.IdentifierName("Kind"),
            SyntaxFactory.IdentifierName("expected"));
        return SyntaxFactory
            .MethodDeclaration(SyntaxFactory.IdentifierName("T"), "GetValue")
            .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.TypeParameter("T"))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("expected"))
                    .WithType(TypeSyntaxEmitter.EmitNamed(union.KindTypeName)))))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.IfStatement(
                    mismatch,
                    SyntaxFactory.ThrowStatement(SyntaxFactory
                        .ObjectCreationExpression(
                            SyntaxFactory.IdentifierName("InvalidOperationException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal("The structural value does not contain the requested arm.")))))))),
                SyntaxFactory.ReturnStatement(SyntaxFactory.CastExpression(
                    SyntaxFactory.IdentifierName("T"),
                    SyntaxFactory.IdentifierName("_value")))));
    }

    internal static MemberAccessExpressionSyntax KindMember(StructuralUnionModelPlan union, string name) =>
        EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.KindTypeName), name);
}
