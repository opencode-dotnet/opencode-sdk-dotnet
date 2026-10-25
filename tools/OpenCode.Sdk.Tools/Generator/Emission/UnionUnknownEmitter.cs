using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionUnknownEmitter
{
    /// <summary>The members a carrier of this union must answer, including the ones its outer unions promise.</summary>
    private static List<HoistedChainMember> ChainHoistedMembers(UnionPlan union) =>
    [
        .. union.Chain.Where(static link => !link.IsTerminal)
            .SelectMany(static link => link.HoistedMembers.Select(member => new HoistedChainMember(link.Name, member)))
            .DistinctBy(static entry => entry.Member.Name, StringComparer.Ordinal),
    ];

    /// <summary>One hoisted member together with the interface that declares it.</summary>
    private sealed record HoistedChainMember(string InterfaceName, HoistedMemberPlan Member);

    internal static GeneratedSource EmitUnknown(UnionPlan union, string relativePath, GeneratedNamespacePlan namespaces)
    {
        var chain = ChainHoistedMembers(union);
        var markerType = TypeSyntaxEmitter.EmitMarker(union.MarkerKind);
        var members = new List<MemberDeclarationSyntax>
        {
            EmitUnknownMarkerField(markerType),
            EmitUnknownConstructor(union, markerType),
            EmitUnknownMarkerProperty(union, markerType),
        };
        if (union.FixedMarker is { } fixedMarker)
        {
            members.Add(EmitFixedMarkerProperty(fixedMarker));
        }

        members.Add(EmitUnknownPayloadProperty());

        // A hoisted member is answered explicitly, so the carrier's own public surface stays the
        // preserved marker and payload and System.Text.Json still sees nothing else on it.
        members.AddRange(chain.Select(EmitUnknownHoistedMember));
        if (union.MasksUnknownPayload)
        {
            members.Add(EmitMaskingToString(union));
        }

        // The concrete-type converter keeps consumer serialization of the carrier itself
        // reproducing the preserved document; without it, source-generated metadata would
        // write the carrier as an ordinary record.
        var declaration = SyntaxFactory
            .RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), union.UnknownTypeName)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(union.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.EmitNamed(union.Name)))))
            .AddAttributeLists(EmissionSyntax.Attribute(
                "JsonConverter",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(
                    TypeSyntaxEmitter.EmitNamed($"{union.UnknownTypeName}JsonConverter")))))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(union.IsStreamFallbackCarrier
                ? $"Preserves an unknown or unmaterializable {GeneratedDisplayName.Of(union.ConceptName)} payload."
                : $"Preserves an unknown {GeneratedDisplayName.Of(union.ConceptName)} payload."));
        var usingNames = union.MarkerKind is LiteralKind.String
            ? new[] { "System", "System.Text.Json", "System.Text.Json.Serialization", namespaces.Internal, }
            : ["System.Text.Json", "System.Text.Json.Serialization", namespaces.Internal];
        declaration = DocumentationSyntax.DocumentedType(declaration, union.UnknownDocumentation);
        var unit = EmissionSyntax.CompilationUnit(
            union.Namespace,
            UnionInterfaceEmitter.HoistedUsings(chain.Select(static entry => entry.Member), usingNames),
            [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }

    /// <summary>
    /// A known arm of this union carries a secret member, so the payload of an arm the pin does
    /// not know may carry one too. The carrier prints itself instead of taking the compiler's
    /// ToString: the same public members in the same order and shape, only the payload masked, so
    /// the marker that names the unrecognized arm stays readable.
    /// </summary>
    private static MethodDeclarationSyntax EmitMaskingToString(UnionPlan union)
    {
        var members = new List<ArgumentSyntax> { RecordPrinterSyntax.Member(union.MarkerName) };
        if (union.FixedMarker is { } fixedMarker)
        {
            members.Add(RecordPrinterSyntax.Member(fixedMarker.Name));
        }

        members.Add(RecordPrinterSyntax.Member("Payload", RecordPrinterSyntax.Redact(SyntaxFactory.IdentifierName("Payload"))));
        return RecordPrinterSyntax.ToStringOverride(
            RecordPrinterSyntax.Format(union.UnknownTypeName, members),
            "Prints the marker with the preserved payload masked, since a known arm of this union carries a secret.");
    }

    private static PropertyDeclarationSyntax EmitUnknownHoistedMember(HoistedChainMember entry) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.Emit(entry.Member.Type), entry.Member.Name)
            .WithExplicitInterfaceSpecifier(SyntaxFactory.ExplicitInterfaceSpecifier(
                SyntaxFactory.IdentifierName(entry.InterfaceName)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

    private static FieldDeclarationSyntax EmitUnknownMarkerField(TypeSyntax markerType) =>
        SyntaxFactory
            .FieldDeclaration(SyntaxFactory
                .VariableDeclaration(markerType)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("_marker"))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static ConstructorDeclarationSyntax EmitUnknownConstructor(UnionPlan union, TypeSyntax markerType)
    {
        var markerParameterName = CSharpNamePolicy.ToCamelCase(union.MarkerName);
        var statements = new List<StatementSyntax>();
        if (union.MarkerKind is LiteralKind.String)
        {
            statements.AddRange(EmissionSyntax.ArgumentNullOrWhiteSpaceGuard(markerParameterName));
        }

        statements.AddRange(EmitUnknownPrefixRefusal(union, markerParameterName));

        // default(JsonElement) has no backing document; Clone would surface a bare
        // InvalidOperationException instead of an argument refusal.
        statements.Add(SyntaxFactory.IfStatement(
            SyntaxFactory.IsPatternExpression(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("payload"), "ValueKind"),
                SyntaxFactory.ConstantPattern(EmissionSyntax.MemberAccess(
                    SyntaxFactory.IdentifierName("JsonValueKind"),
                    "Undefined"))),
            SyntaxFactory.Block(EmissionSyntax.ThrowArgumentException("The payload must be a parsed JSON element.", "payload"))));

        statements.AddRange(EmitPayloadMarkerChecks(union, markerParameterName));

        statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName("_marker"),
            SyntaxFactory.IdentifierName(markerParameterName))));
        statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName("Payload"),
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("payload"), "Clone")))));
        return SyntaxFactory
            .ConstructorDeclaration(union.UnknownTypeName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonConstructor"))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(markerParameterName)).WithType(markerType),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("payload")).WithType(SyntaxFactory.IdentifierName("JsonElement")),
            ])))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Initializes an unknown union value from its marker and raw payload."));
    }

    /// <summary>
    /// The carrier refuses markers claimed by the prefix-tagged arm at construction:
    /// the untyped twin of that arm's own marker refusing a value
    /// outside its prefix.
    /// </summary>
    private static IReadOnlyList<StatementSyntax> EmitUnknownPrefixRefusal(UnionPlan union, string markerParameterName)
    {
        if (union.PrefixVariant is not { } prefix)
        {
            return [];
        }

        return
        [
            SyntaxFactory.IfStatement(
                EmissionSyntax.StartsWithOrdinal(SyntaxFactory.IdentifierName(markerParameterName), prefix.Prefix),
                SyntaxFactory.Block(EmissionSyntax.ThrowArgumentException(
                    $"The '{union.MarkerWireName}' marker is claimed by the '{prefix.Prefix}' prefix-tagged arm and cannot be carried as unknown.",
                    markerParameterName))),
        ];
    }

    private static List<StatementSyntax> EmitPayloadMarkerChecks(UnionPlan union, string markerParameterName)
    {
        var checks = new List<StatementSyntax>();
        if (union.FixedMarker is { } fixedMarker)
        {
            checks.Add(EmitPayloadMarkerCheck(
                fixedMarker.Kind,
                fixedMarker.WireName,
                EmissionSyntax.MarkerLiteral(fixedMarker.Kind, fixedMarker.Value)));
        }

        checks.Add(EmitPayloadMarkerCheck(
            union.MarkerKind,
            union.MarkerWireName,
            SyntaxFactory.IdentifierName(markerParameterName)));
        return checks;
    }

    private static ExpressionStatementSyntax EmitPayloadMarkerCheck(LiteralKind kind, string wireName, ExpressionSyntax expected)
    {
        var methodName = kind switch
        {
            LiteralKind.String => "RequireString",
            LiteralKind.Boolean => "RequireBoolean",
            LiteralKind.Number or _ => throw new InvalidOperationException(
                $"Union payload marker '{wireName}' has unsupported kind '{kind}'."),
        };
        return SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("UnionPayloadGuard"), "Instance"),
                methodName),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("payload")),
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(wireName))),
            SyntaxFactory.Argument(expected)));
    }

    /// <summary>
    /// Emits the unknown carrier's marker property. The <c>JsonPropertyName</c> attribute names
    /// the union's marker and is inert - the carrier is read and written by the union's converter,
    /// never by System.Text.Json's property machinery.
    /// </summary>
    private static PropertyDeclarationSyntax EmitUnknownMarkerProperty(UnionPlan union, TypeSyntax markerType) =>
        SyntaxFactory
            .PropertyDeclaration(markerType, union.MarkerName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(union.MarkerWireName)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName("_marker")))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(union.IsStreamFallbackCarrier
                ? $"Gets the preserved {MarkerDescription(union)} marker."
                : $"Gets the unrecognized {MarkerDescription(union)} marker."));

    private static PropertyDeclarationSyntax EmitFixedMarkerProperty(UnionFixedMarkerPlan marker) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitMarker(marker.Kind), marker.Name)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(marker.WireName)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.MarkerLiteral(marker.Kind, marker.Value)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation($"Gets the fixed '{marker.WireName}' marker of this nested union."));

    private static PropertyDeclarationSyntax EmitUnknownPayloadProperty() =>
        SyntaxFactory
            .PropertyDeclaration(SyntaxFactory.IdentifierName("JsonElement"), "Payload")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(EmissionSyntax.GetOnlyAccessors())
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Gets the preserved raw JSON payload."));

    /// <summary>Quotes the union's marker property for the messages that refuse a payload.</summary>
    internal static string MarkerDescription(UnionPlan union) => $"'{union.MarkerWireName}'";
}
