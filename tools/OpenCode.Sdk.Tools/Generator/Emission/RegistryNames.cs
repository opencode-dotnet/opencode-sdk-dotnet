using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class RegistryNames
{
    public const string TypeName = "OpenCodeJsonContext";

    public static ExpressionSyntax Default =>
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName(TypeName), "Default");
}
