using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionInterfaceEmitter
{
    internal static GeneratedSource EmitBase(UnionPlan union, string relativePath, GeneratedNamespacePlan namespaces)
    {
        var converterTypeName = $"{union.ConceptName}JsonConverter";

        // The union is an interface because a wire schema can be a branch of more than one
        // union, which a base class cannot express (ADR-0011).
        var marker = SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitMarker(union.MarkerKind), union.MarkerName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(union.MarkerWireName)))
            .WithAccessorList(EmissionSyntax.GetOnlyAccessors())
            .WithLeadingTrivia(DocumentationSyntax.Documentation($"Gets the '{union.MarkerWireName}' union marker."));
        // A union inherits a matching discriminator from its generated parent or the stream-cause
        // marker; redeclaring it would hide the member its base already promises.
        var declaresMarker = !InheritsMarker(union);
        // Every member declares the hoisted properties in the same shape, so the interface can
        // promise them; each is nullable because the unknown carrier materializes none of them.
        var members = new List<MemberDeclarationSyntax>();
        if (declaresMarker)
        {
            members.Add(marker);
        }

        members.AddRange(union.HoistedMembers.Select(member => EmitHoistedMember(union, member)));
        var declaration = SyntaxFactory
            .InterfaceDeclaration(union.Name)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(union.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute(
                "JsonConverter",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(TypeSyntaxEmitter.EmitNamed(converterTypeName)))))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(DocumentationSyntax.MemberDocumentation(
                union.Description ?? $"Represents {GeneratedDisplayName.WithIndefiniteArticle(union.ConceptName)} union.",
                [],
                remarks: PinBoundInterfaceRemarks.Runs));
        if (union.BaseTypeName is not null)
        {
            declaration = declaration.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.EmitNamed(union.BaseTypeName)))));
        }

        var documentation = union.Documentation with
        {
            Members = union.Documentation.Members.ToDictionary(static pair => pair.Key, pair =>
            {
                if (union.HoistedMembers.FirstOrDefault(member => string.Equals(member.Name, pair.Key, StringComparison.Ordinal)) is not { } member)
                {
                    return pair.Value;
                }

                return pair.Value with
                {
                    Remarks = pair.Value.Remarks + " Every declared variant carries it; an unrecognized variant preserved as "
                    + union.UnknownTypeName + " returns null through this interface."
                    + (member.IsRequired ? "" : " An ordinary optional property may also be absent.")
                };
            }, StringComparer.Ordinal),
        };
        declaration = DocumentationSyntax.DocumentedType(declaration, documentation, PinBoundInterfaceRemarks.Runs);
        var unit = EmissionSyntax.CompilationUnit(
            union.Namespace,
            HoistedUsings(union.HoistedMembers, [namespaces.Internal, "System.Text.Json.Serialization"]),
            [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }

    private static PropertyDeclarationSyntax EmitHoistedMember(UnionPlan union, HoistedMemberPlan member)
    {
        var summary = member.Description ?? GeneratedDisplayName.MemberSummary(member.Name);
        return HoistedInterfaceEmitter.MemberProperty(member,
            summary + " Every declared variant carries it; the value is null when the payload is an "
            + $"unrecognized variant preserved as {union.UnknownTypeName}.");
    }

    internal static string[] HoistedUsings(IEnumerable<HoistedMemberPlan> members, IEnumerable<string> seed)
    {
        var result = new HashSet<string>(seed, StringComparer.Ordinal);
        foreach (var member in members)
        {
            TypeUsingCollector.Collect(member.Type, result);
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }

    private static bool InheritsMarker(UnionPlan union) => union.Chain.Skip(1).Any(link =>
        string.Equals(link.MarkerWireName, union.MarkerWireName, StringComparison.Ordinal)
        && link.MarkerKind == union.MarkerKind);
}
