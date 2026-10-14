using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits the interface a hoisted promoted-object member is declared with. It is a plain
/// interface: no converter, no unknown arm, and no serializer registration, because nothing
/// deserializes into it — the records it stands for keep their own identity and their own
/// metadata. Its members are therefore exactly as the records declare them.
/// </summary>
internal static class HoistedInterfaceEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<HoistedInterfacePlan> interfaces, ModelPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(placement);
        return Array.AsReadOnly([
            .. interfaces
                .OrderBy(static plan => plan.Name, StringComparer.Ordinal)
                .Select(plan => Emit(plan, GeneratedPlacement.ModelPathOf(placement, plan.Name))),
        ]);
    }

    private static GeneratedSource Emit(HoistedInterfacePlan plan, string relativePath)
    {
        var declaration = SyntaxFactory
            .InterfaceDeclaration(plan.Name)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(plan.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>([.. plan.Members.Select(EmitMember)]))
            .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
                plan.Description ?? $"Represents a {plan.Name} value.",
                [],
                remarks: PinBoundInterfaceRemarks.Runs));
        declaration = EmissionSyntax.DocumentedType(declaration, plan.Documentation, PinBoundInterfaceRemarks.Runs);
        var unit = EmissionSyntax.CompilationUnit(plan.Namespace, CollectUsings(plan), [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }

    private static PropertyDeclarationSyntax EmitMember(HoistedMemberPlan member) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.Emit(member.Type), member.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(member.WireName)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory
                    .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                member.Description ?? $"Gets the {GeneratedDisplayName.Of(member.Name)} value."));

    private static IReadOnlyList<string> CollectUsings(HoistedInterfacePlan plan)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { "System.Text.Json.Serialization" };
        foreach (var member in plan.Members)
        {
            TypeUsingCollector.Collect(member.Type, result);
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }
}
