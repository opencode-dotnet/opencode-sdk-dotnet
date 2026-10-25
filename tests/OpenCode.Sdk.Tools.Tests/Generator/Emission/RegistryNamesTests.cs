using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Emission;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class RegistryNamesTests
{
    [Test]
    public async Task Default_Should_Keep_The_Identifier_Member_Access_Shape()
    {
        var node = RegistryNames.Default;
        await Assert.That(node.Kind()).IsEqualTo(SyntaxKind.SimpleMemberAccessExpression);
        var member = (MemberAccessExpressionSyntax)node;
        await Assert.That(member.Expression.Kind()).IsEqualTo(SyntaxKind.IdentifierName);
        await Assert.That(member.Name.Kind()).IsEqualTo(SyntaxKind.IdentifierName);
        await Assert.That(node.ToFullString()).IsEqualTo("OpenCodeJsonContext.Default");
    }
}
