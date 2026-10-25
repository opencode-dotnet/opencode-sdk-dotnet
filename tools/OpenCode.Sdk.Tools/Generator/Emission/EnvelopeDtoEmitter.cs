using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits the internal single-pass deserialization DTO of every wrapped envelope; required
/// properties make a missing wire member a deserialization failure, so the adapters'
/// protocol walls hold without a second parse.
/// </summary>
internal static class EnvelopeDtoEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ClientPlan> clients, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(namespaces);

        return Array.AsReadOnly(
        [
            .. EnvelopeOperations.Naming(clients, static envelope => envelope.EnvelopeDtoTypeName)
                .Select(operation => EmitDto(operation.Envelope!, TypeHome.Family(operation.RouteContainerName), namespaces)),
        ]);
    }

    private static GeneratedSource EmitDto(EnvelopePlan envelope, TypeHome owner, GeneratedNamespacePlan namespaces)
    {
        // The DTO member stays 'Data' whatever the wire key is: it is internal single-pass
        // framing every adapter reads the same way, and only the JSON name is the contract.
        var data = EmitProperty(envelope.WireMemberName, "Data", TypeSyntaxEmitter.Emit(envelope.PayloadType!));
        var members = new List<MemberDeclarationSyntax> { data, };
        if (envelope.Kind is EnvelopeKind.CursorList)
        {
            members.Add(EmitProperty("cursor", "Cursor", TypeSyntaxEmitter.EmitNamed("ListCursor")));
        }

        if (envelope.Kind is EnvelopeKind.DataLocation or EnvelopeKind.DataLocationList)
        {
            var locationTypeName = envelope.LocationTypeName
                                   ?? throw new InvalidOperationException($"Envelope '{envelope.ResponseTypeName}' has no location sibling.");
            members.Add(EmitProperty("location", "Location", TypeSyntaxEmitter.EmitNamed(locationTypeName)));
        }

        var declaration = SyntaxFactory.RecordDeclaration(
                SyntaxFactory.Token(SyntaxKind.RecordKeyword),
                envelope.EnvelopeDtoTypeName!)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                $"Carries the wire body of '{envelope.ResponseTypeName}' through one deserialization pass."));
        var unit = EmissionSyntax.CompilationUnit(
            namespaces.Internal,
            ["System.Text.Json.Serialization", namespaces.Models],
            [declaration]);
        return EmissionSyntax.CreateSource(GeneratedPlacement.PathOf(GeneratedKind.EnvelopeDto, owner, envelope.EnvelopeDtoTypeName!), unit);
    }

    private static PropertyDeclarationSyntax EmitProperty(string wireName, string name, TypeSyntax type) =>
        SyntaxFactory.PropertyDeclaration(type, name)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.RequiredKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(
            [
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            ])))
            .AddAttributeLists(EmissionSyntax.Attribute(
                "JsonPropertyName",
                SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(wireName)))));
}
