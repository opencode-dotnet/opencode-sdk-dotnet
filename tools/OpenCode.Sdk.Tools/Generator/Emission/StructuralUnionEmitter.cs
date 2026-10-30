using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class StructuralUnionEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<StructuralUnionModelPlan> unions, ModelPlacement placement, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(unions);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(namespaces);

        var result = new List<GeneratedSource>(unions.Count * 3);
        foreach (var union in unions.OrderBy(static union => union.Name, StringComparer.Ordinal))
        {
            result.Add(EmitKind(union, GeneratedPlacement.ModelPathOf(placement, union.KindTypeName)));
            result.Add(StructuralUnionCarrierEmitter.EmitCarrier(union, GeneratedPlacement.ModelPathOf(placement, union.Name), namespaces));
            result.Add(StructuralUnionConverterEmitter.EmitConverter(union, GeneratedPlacement.ConverterPathOf(placement, union.Name, ConverterNamePolicy.Of(union.Name)), namespaces));
        }

        return Array.AsReadOnly([.. result]);
    }

    private static GeneratedSource EmitKind(StructuralUnionModelPlan union, string relativePath)
    {
        var members = union
            .Arms.Select(static arm => arm.Name)
            .Append("Unknown")
            .Select(name => SyntaxFactory
                .EnumMemberDeclaration(name)
                .WithLeadingTrivia(DocumentationSyntax.Documentation($"Represents the {GeneratedDisplayName.Of(name)} arm.")));
        var declaration = SyntaxFactory
            .EnumDeclaration(union.KindTypeName)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(union.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.SeparatedList(members))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                union.KindDocumentation.Summary ?? $"Identifies the active arm of {union.Name}.", union.KindDocumentation.Remarks));
        var unit = EmissionSyntax.CompilationUnit(union.Namespace, [], [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }
}
