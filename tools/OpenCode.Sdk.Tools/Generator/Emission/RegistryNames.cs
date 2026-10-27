using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class RegistryNames
{
    public const string TypeName = ReservedNamePolicy.JsonContextType;

    public static ExpressionSyntax Default =>
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName(TypeName), "Default");
}
