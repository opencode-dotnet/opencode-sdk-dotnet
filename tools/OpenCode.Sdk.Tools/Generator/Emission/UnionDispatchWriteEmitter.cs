using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionDispatchWriteEmitter
{
    internal static MethodDeclarationSyntax EmitWrite(UnionPlan union)
    {
        var unknownPattern = SyntaxFactory.IsPatternExpression(
            SyntaxFactory.IdentifierName("value"),
            SyntaxFactory.DeclarationPattern(
                SyntaxFactory.IdentifierName(union.UnknownTypeName),
                SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("unknown"))));
        var writeUnknown = SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("unknown"), "Payload"),
                    "WriteTo"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("writer")))),
            SyntaxFactory.ReturnStatement());
        var getRuntimeType = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "GetType"));
        var getTypeInfo = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(
                RegistryNames.Default,
                "GetTypeInfo"),
            SyntaxFactory.Argument(getRuntimeType));
        var typeInfo = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            getTypeInfo,
            SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException($"The generated context has no metadata for {union.ConceptName}.")));
        var serialize = ConverterSyntax.SerializeWithTypeInfo(
            SyntaxFactory.IdentifierName("value"), SyntaxFactory.IdentifierName("typeInfo"));
        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.IfStatement(unknownPattern, writeUnknown),
        };

        // A nested-union variant must serialize through its declared base type so the nested
        // union's converter runs (its unknown carrier writes the raw payload); runtime-type
        // metadata would serialize the carrier as an ordinary record.
        foreach (var typeName in union.Variants.Where(static variant => variant.IsNestedUnion).Select(static variant => variant.TypeName))
        {
            var variableName = $"nested{typeName}";
            var nestedPattern = SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("value"),
                SyntaxFactory.DeclarationPattern(
                    SyntaxFactory.IdentifierName(typeName),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(variableName))));
            var nestedSerialize = ConverterSyntax.SerializeWithTypeInfo(
                SyntaxFactory.IdentifierName(variableName),
                EmissionSyntax.MemberAccess(RegistryNames.Default, typeName));
            statements.Add(SyntaxFactory.IfStatement(
                nestedPattern,
                SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(nestedSerialize),
                    SyntaxFactory.ReturnStatement())));
        }

        statements.Add(EmissionSyntax.Local("typeInfo", typeInfo));
        statements.Add(SyntaxFactory.ExpressionStatement(serialize));
        return ConverterSyntax.Write(TypeSyntaxEmitter.EmitNamed(union.Name), SyntaxFactory.Block(statements), ["writer", "value", "options"]);
    }
}
