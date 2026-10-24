using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class RegistryEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(RegistryPlan plan, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(namespaces);
        var declaration = SyntaxFactory.ClassDeclaration(RegistryNames.TypeName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword),
                SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName("JsonSerializerContext")))))
            .AddAttributeLists(EmissionSyntax.Attribute(
                "JsonSourceGenerationOptions",
                SyntaxFactory.AttributeArgument(EmissionSyntax.MemberAccess(
                        SyntaxFactory.IdentifierName("JsonSourceGenerationMode"),
                        "Metadata"))
                    .WithNameEquals(SyntaxFactory.NameEquals("GenerationMode")),
                SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))
                    .WithNameEquals(SyntaxFactory.NameEquals("RespectNullableAnnotations")),
                SyntaxFactory.AttributeArgument(EmissionSyntax.MemberAccess(
                        SyntaxFactory.IdentifierName("JsonUnmappedMemberHandling"),
                        "Skip"))
                    .WithNameEquals(SyntaxFactory.NameEquals("UnmappedMemberHandling"))));
        foreach (var typeName in plan.TypeNames.Order(StringComparer.Ordinal))
        {
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonSerializable",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(TypeSyntaxEmitter.EmitNamed(typeName)))));
        }

        var payloadEntries = plan.PayloadEntries
            .Select(entry => (Entry: entry, Accessor: SerializerTypeNamePolicy.ContextPropertyName(entry)))
            .DistinctBy(static item => item.Accessor, StringComparer.Ordinal)
            .OrderBy(static item => item.Accessor, StringComparer.Ordinal);
        foreach (var (entry, accessor) in payloadEntries)
        {
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonSerializable",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(TypeSyntaxEmitter.Emit(entry))),
                EmissionSyntax.StringArgument(accessor).WithNameEquals(SyntaxFactory.NameEquals("TypeInfoPropertyName"))));
        }

        var usings = new List<string> { namespaces.Models, "System.Text.Json.Serialization" };
        if (plan.PayloadEntries.Count > 0)
        {
            usings.Add("System.Collections.Generic");
        }

        var unit = EmissionSyntax.CompilationUnit(
            namespaces.Internal,
            usings,
            [declaration]);
        return Array.AsReadOnly(
        [
            EmissionSyntax.CreateSource(GeneratedPlacement.PathOf(GeneratedKind.SharedSerialization, null, RegistryNames.TypeName), unit),
        ]);
    }
}
