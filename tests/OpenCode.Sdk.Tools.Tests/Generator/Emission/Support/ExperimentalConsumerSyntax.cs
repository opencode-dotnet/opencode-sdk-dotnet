using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

internal static class ExperimentalConsumerSyntax
{
    public static SyntaxTree CreateTree()
    {
        var call = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("client"), SyntaxFactory.IdentifierName("Experimental")),
            SyntaxFactory.IdentifierName("GetMigrationV1StatusAsync")));
        var method = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName("System.Threading.Tasks.Task"), "Probe")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("client")).WithType(SyntaxFactory.ParseTypeName("OpenCode.Sdk.OpenCodeClient")))))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(call)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var unit = SyntaxFactory.CompilationUnit().AddMembers(SyntaxFactory.ClassDeclaration("Consumer")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .AddMembers(method));
        return CSharpSyntaxTree.Create(unit, new CSharpParseOptions(LanguageVersion.CSharp14));
    }
}
