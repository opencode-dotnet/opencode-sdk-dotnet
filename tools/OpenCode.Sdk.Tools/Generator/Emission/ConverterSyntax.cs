using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class ConverterSyntax
{
    public static ClassDeclarationSyntax Declaration(string name, TypeSyntax valueType,
        IEnumerable<MemberDeclarationSyntax> members, SyntaxTriviaList documentation) => SyntaxFactory
        .ClassDeclaration(name)
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.InternalKeyword),
            SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
        .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
            SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic("JsonConverter", valueType)))))
        .WithMembers(SyntaxFactory.List(members))
        .WithLeadingTrivia(documentation);

    public static MethodDeclarationSyntax Read(TypeSyntax valueType, BlockSyntax body, IReadOnlyList<string> guardedParameters) => SyntaxFactory
        .MethodDeclaration(valueType, "Read")
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
        [
            SyntaxFactory
                .Parameter(SyntaxFactory.Identifier("reader"))
                .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader"))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("typeToConvert")).WithType(SyntaxFactory.IdentifierName("Type")),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(SyntaxFactory.IdentifierName("JsonSerializerOptions")),
        ])))
        .WithBody(GuardedBody(body, guardedParameters));

    public static MethodDeclarationSyntax Write(TypeSyntax valueType, BlockSyntax body, IReadOnlyList<string> guardedParameters) => SyntaxFactory
        .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), "Write")
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
        [
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("writer")).WithType(SyntaxFactory.IdentifierName("Utf8JsonWriter")),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(valueType),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(SyntaxFactory.IdentifierName("JsonSerializerOptions")),
        ])))
        .WithBody(GuardedBody(body, guardedParameters));

    public static LocalDeclarationStatementSyntax PayloadDocument()
    {
        var parse = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonDocument"), "ParseValue"),
            SyntaxFactory
                .Argument(SyntaxFactory.IdentifierName("reader"))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)));
        return SyntaxFactory
            .LocalDeclarationStatement(SyntaxFactory
                .VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator("document").WithInitializer(SyntaxFactory.EqualsValueClause(parse)))))
            .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword));
    }

    public static InvocationExpressionSyntax SerializeWithTypeInfo(ExpressionSyntax value, ExpressionSyntax typeInfo) =>
        EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonSerializer"), "Serialize"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("writer")),
            SyntaxFactory.Argument(value),
            SyntaxFactory.Argument(typeInfo));

    internal static ThrowStatementSyntax ThrowJson(string message) => SyntaxFactory.ThrowStatement(EmissionSyntax.JsonException(message));

    private static BlockSyntax GuardedBody(BlockSyntax body, IReadOnlyList<string> guardedParameters) =>
        body.WithStatements(SyntaxFactory.List(guardedParameters.SelectMany(EmissionSyntax.ArgumentNullGuard).Concat(body.Statements)));
}
