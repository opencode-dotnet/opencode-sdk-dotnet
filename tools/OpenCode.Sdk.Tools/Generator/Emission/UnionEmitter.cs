using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<UnionPlan> unions, ModelPlacement placement, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(unions);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(namespaces);
        var result = new List<GeneratedSource>(unions.Count * 4);
        foreach (var union in unions.OrderBy(static union => union.Name, StringComparer.Ordinal))
        {
            result.Add(EmitBase(union, GeneratedPlacement.ModelPathOf(placement, union.Name), namespaces));
            result.Add(EmitUnknown(union, GeneratedPlacement.ModelPathOf(placement, union.UnknownTypeName), namespaces));
            result.Add(EmitConverter(union, placement, namespaces));
            result.Add(EmitCarrierConverter(union, placement, namespaces));
        }

        return Array.AsReadOnly([.. result]);
    }

    private static GeneratedSource EmitBase(UnionPlan union, string relativePath, GeneratedNamespacePlan namespaces)
    {
        var converterTypeName = $"{union.ConceptName}JsonConverter";

        // The union is an interface because a wire schema can be a branch of more than one
        // union, which a base class cannot express (ADR-0011).
        var marker = SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitMarker(union.MarkerKind), union.MarkerName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(union.MarkerWireName)))
            .WithAccessorList(EmissionSyntax.GetOnlyAccessors())
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Gets the '{union.MarkerWireName}' union marker."));
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
            .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
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
        declaration = EmissionSyntax.DocumentedType(declaration, documentation, PinBoundInterfaceRemarks.Runs);
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

    /// <summary>The members a carrier of this union must answer, including the ones its outer unions promise.</summary>
    private static List<HoistedChainMember> ChainHoistedMembers(UnionPlan union) =>
    [
        .. union.Chain.Where(static link => !link.IsTerminal)
            .SelectMany(static link => link.HoistedMembers.Select(member => new HoistedChainMember(link.Name, member)))
            .DistinctBy(static entry => entry.Member.Name, StringComparer.Ordinal),
    ];

    private static string[] HoistedUsings(IEnumerable<HoistedMemberPlan> members, IEnumerable<string> seed)
    {
        var result = new HashSet<string>(seed, StringComparer.Ordinal);
        foreach (var member in members)
        {
            TypeUsingCollector.Collect(member.Type, result);
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }

    /// <summary>One hoisted member together with the interface that declares it.</summary>
    private sealed record HoistedChainMember(string InterfaceName, HoistedMemberPlan Member);

    private static bool InheritsMarker(UnionPlan union) => union.Chain.Skip(1).Any(link =>
        string.Equals(link.MarkerWireName, union.MarkerWireName, StringComparison.Ordinal)
        && link.MarkerKind == union.MarkerKind);

    private static GeneratedSource EmitUnknown(UnionPlan union, string relativePath, GeneratedNamespacePlan namespaces)
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
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Preserves an unknown {GeneratedDisplayName.Of(union.ConceptName)} payload."));
        var usingNames = union.MarkerKind is LiteralKind.String
            ? new[] { "System", "System.Text.Json", "System.Text.Json.Serialization", namespaces.Internal, }
            : ["System.Text.Json", "System.Text.Json.Serialization", namespaces.Internal];
        declaration = EmissionSyntax.DocumentedType(declaration, union.UnknownDocumentation);
        var unit = EmissionSyntax.CompilationUnit(
            union.Namespace,
            HoistedUsings(chain.Select(static entry => entry.Member), usingNames),
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

    /// <summary>
    /// The carrier's own converter: reading reproduces the base converter's fallback arm,
    /// writing replays the preserved document.
    /// </summary>
    private static GeneratedSource EmitCarrierConverter(UnionPlan union, ModelPlacement placement, GeneratedNamespacePlan namespaces)
    {
        var converterName = $"{union.UnknownTypeName}JsonConverter";
        var readStatements = new List<StatementSyntax>();
        readStatements.AddRange(
        [
            ConverterSyntax.PayloadDocument(),
            EmissionSyntax.Local("payload", EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("document"), "RootElement")),
            EmitObjectPayloadCheck(union),
        ]);
        readStatements.AddRange(EmitFixedMarkerCheck(union));
        readStatements.Add(EmitMarkerPresenceCheck(union));
        readStatements.AddRange(EmitMarkerRead(union));
        readStatements.AddRange(EmitCarrierPrefixRefusal(union));
        readStatements.Add(EmitUnknownReturn(union));
        var read = ConverterSyntax.Read(TypeSyntaxEmitter.EmitNamed(union.UnknownTypeName),
            SyntaxFactory.Block(readStatements), ["typeToConvert", "options"]);

        var writeStatements = new List<StatementSyntax>
        {
            SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "Payload"),
                    "WriteTo"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("writer")))),
        };
        var write = ConverterSyntax.Write(TypeSyntaxEmitter.EmitNamed(union.UnknownTypeName),
            SyntaxFactory.Block(writeStatements), ["writer", "value", "options"]);

        var declaration = ConverterSyntax.Declaration(converterName,
            TypeSyntaxEmitter.EmitNamed(union.UnknownTypeName), [read, write], default);
        var unit = EmissionSyntax.CompilationUnit(
            namespaces.Internal,
            [
                namespaces.Models,
                "System",
                "System.Text.Json",
                "System.Text.Json.Serialization",
            ],
            [declaration]);
        return EmissionSyntax.CreateSource(GeneratedPlacement.ConverterPathOf(placement, union.Name, converterName), unit);
    }

    private static ReturnStatementSyntax EmitUnknownReturn(UnionPlan union) =>
        SyntaxFactory.ReturnStatement(SyntaxFactory
            .ObjectCreationExpression(SyntaxFactory.IdentifierName(union.UnknownTypeName))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("marker")),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("payload")),
            ]))));

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
            .WithLeadingTrivia(EmissionSyntax.Documentation("Initializes an unknown union value from its marker and raw payload."));
    }

    /// <summary>
    /// The carrier holds markers no arm claims, so a marker the prefix-tagged arm claims is
    /// refused at construction: the untyped twin of that arm's own marker refusing a value
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

    /// <summary>
    /// The carrier converter refuses a prefix-claimed marker as malformed JSON before the
    /// carrier's constructor can, so the refusal surfaces as the converter's own exception
    /// rather than as a BCL argument escape.
    /// </summary>
    private static IReadOnlyList<StatementSyntax> EmitCarrierPrefixRefusal(UnionPlan union)
    {
        if (union.PrefixVariant is not { } prefix)
        {
            return [];
        }

        return
        [
            SyntaxFactory.IfStatement(
                EmissionSyntax.StartsWithOrdinal(SyntaxFactory.IdentifierName("marker"), prefix.Prefix),
                ThrowJson($"The {union.ConceptName} payload carries the '{prefix.Prefix}' prefix-tagged arm and is not an unknown {GeneratedDisplayName.Of(union.ConceptName)}.")),
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
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Gets the unrecognized {MarkerDescription(union)} marker."));

    private static PropertyDeclarationSyntax EmitFixedMarkerProperty(UnionFixedMarkerPlan marker) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitMarker(marker.Kind), marker.Name)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(marker.WireName)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.MarkerLiteral(marker.Kind, marker.Value)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Gets the fixed '{marker.WireName}' marker of this nested union."));

    private static PropertyDeclarationSyntax EmitUnknownPayloadProperty() =>
        SyntaxFactory
            .PropertyDeclaration(SyntaxFactory.IdentifierName("JsonElement"), "Payload")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(EmissionSyntax.GetOnlyAccessors())
            .WithLeadingTrivia(EmissionSyntax.Documentation("Gets the preserved raw JSON payload."));

    private static GeneratedSource EmitConverter(UnionPlan union, ModelPlacement placement, GeneratedNamespacePlan namespaces)
    {
        var converterName = $"{union.ConceptName}JsonConverter";
        var declaration = ConverterSyntax.Declaration(converterName, TypeSyntaxEmitter.EmitNamed(union.Name),
            [
                .. EmitDispatchMaps(union),
                EmitDiscriminatorReader(),
                EmitRead(union),
                EmitWrite(union),
            ], default);
        var unit = EmissionSyntax.CompilationUnit(
            namespaces.Internal,
            [
                namespaces.Models,
                "System",
                "System.Collections.Frozen",
                "System.Collections.Generic",
                "System.Text.Json",
                "System.Text.Json.Serialization",
            ],
            [declaration]);
        return EmissionSyntax.CreateSource(GeneratedPlacement.ConverterPathOf(placement, union.Name, converterName), unit);
    }

    /// <summary>
    /// The frozen table of the union's declared tags; an impossible tag maps to no type.
    /// </summary>
    private static List<MemberDeclarationSyntax> EmitDispatchMaps(UnionPlan union)
    {
        var valueType = union.KnownImpossibleTags.Count > 0
            ? (TypeSyntax)SyntaxFactory.NullableType(SyntaxFactory.IdentifierName("Type"))
            : SyntaxFactory.IdentifierName("Type");
        return [EmitDispatchMap(union, valueType)];
    }

    private static FieldDeclarationSyntax EmitDispatchMap(UnionPlan union, TypeSyntax valueType)
    {
        var markerType = TypeSyntaxEmitter.EmitMarker(union.MarkerKind);
        var dictionaryType = TypeSyntaxEmitter.Generic("Dictionary", markerType, valueType);
        var entries = union
            .Variants
            .Where(variant => string.Equals(variant.MarkerWireName, union.MarkerWireName, StringComparison.Ordinal))
            .Select(static variant => new KeyValuePair<string, string?>(variant.Tag, variant.TypeName))
            .Concat(union.KnownImpossibleTags.Select(static tag => new KeyValuePair<string, string?>(tag, null)))
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (ExpressionSyntax)SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.ImplicitElementAccess(SyntaxFactory.BracketedArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(EmissionSyntax.MarkerLiteral(union.MarkerKind, entry.Key))))),
                entry.Value is null
                    ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                    : SyntaxFactory.TypeOfExpression(TypeSyntaxEmitter.EmitNamed(entry.Value))))
            .ToArray();
        var initializer = SyntaxFactory
            .ObjectCreationExpression(dictionaryType)
            .WithArgumentList(union.MarkerKind is LiteralKind.String
                ? SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StringComparer"), "Ordinal"))))
                : SyntaxFactory.ArgumentList())
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.CollectionInitializerExpression,
                SyntaxFactory.SeparatedList(entries)));
        // The table is built once and read per payload, so it freezes for lookup speed; the
        // comparer rides along because ToFrozenDictionary does not inherit the source's.
        var frozen = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(initializer, "ToFrozenDictionary"),
            union.MarkerKind is LiteralKind.String
                ?
                [
                    SyntaxFactory.Argument(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StringComparer"), "Ordinal")),
                ]
                : []);
        return SyntaxFactory
            .FieldDeclaration(SyntaxFactory
                .VariableDeclaration(TypeSyntaxEmitter.Generic("FrozenDictionary", markerType, valueType))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory
                        .VariableDeclarator("TypesByTag")
                        .WithInitializer(SyntaxFactory.EqualsValueClause(frozen)))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));
    }

    /// <summary>Quotes the union's marker property for the messages that refuse a payload.</summary>
    private static string MarkerDescription(UnionPlan union) => $"'{union.MarkerWireName}'";

    private static MethodDeclarationSyntax EmitRead(UnionPlan union)
    {
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmitReaderFixedMarkerCheck(union));
        // String markers dispatch through the reader's combined lookup, which only
        // materializes the marker string on the unknown path; other kinds read it first.
        if (union.MarkerKind is not LiteralKind.String)
        {
            statements.Add(EmitReaderMarkerRead(union));
        }

        statements.Add(EmitKnownDispatch(union));
        statements.AddRange(EmitPrefixDispatch(union));
        statements.Add(ConverterSyntax.PayloadDocument());
        statements.Add(EmissionSyntax.Local("payload", EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("document"), "RootElement")));
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory
            .ObjectCreationExpression(
                SyntaxFactory.IdentifierName(union.UnknownTypeName))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("marker")),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("payload")),
            ])))));

        return ConverterSyntax.Read(TypeSyntaxEmitter.EmitNamed(union.Name), SyntaxFactory.Block(statements), ["typeToConvert", "options"]);
    }

    private static FieldDeclarationSyntax EmitDiscriminatorReader() =>
        SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("UnionDiscriminatorReader"),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("DiscriminatorReader")
                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.IdentifierName("UnionDiscriminatorReader"))
                        .WithArgumentList(SyntaxFactory.ArgumentList()))))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static IReadOnlyList<StatementSyntax> EmitReaderFixedMarkerCheck(UnionPlan union)
    {
        if (union.FixedMarker is not { } marker)
        {
            return [];
        }

        var methodName = marker.Kind switch
        {
            LiteralKind.String => "RequireString",
            LiteralKind.Boolean => "RequireBoolean",
            LiteralKind.Number or _ => throw new InvalidOperationException(
                $"Union '{union.ConceptName}' fixes marker '{marker.WireName}' with kind '{marker.Kind}', which has no emission consumer."),
        };
        return
        [
            SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("DiscriminatorReader"), methodName),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader"))
                    .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
                RuntimeStringArgument(marker.WireName),
                SyntaxFactory.Argument(EmissionSyntax.MarkerLiteral(marker.Kind, marker.Value)),
                RuntimeStringArgument(union.ConceptName))),
        ];
    }

    private static LocalDeclarationStatementSyntax EmitReaderMarkerRead(UnionPlan union)
    {
        var methodName = union.MarkerKind switch
        {
            LiteralKind.String => throw new InvalidOperationException(
                $"Union '{union.ConceptName}' uses a string marker, which dispatches through TryFindKnown."),
            LiteralKind.Boolean => "ReadBoolean",
            LiteralKind.Number or _ => throw new InvalidOperationException(
                $"Union '{union.ConceptName}' uses a number marker, which has no emission consumer."),
        };
        return EmissionSyntax.Local("marker", EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("DiscriminatorReader"), methodName),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader"))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            RuntimeStringArgument(union.MarkerWireName),
            RuntimeStringArgument(union.ConceptName)));
    }

    private static ArgumentSyntax RuntimeStringArgument(string value) =>
        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(value)));

    private static ArgumentSyntax OutVariableArgument(string name) =>
        SyntaxFactory
            .Argument(SyntaxFactory.DeclarationExpression(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier(name))))
            .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));

    /// <summary>
    /// A nested union's fixed outer tag is structural identity, not dispatch input: a
    /// foreign value is a malformed payload, while the inner marker keeps its
    /// unknown-variant tolerance.
    /// </summary>
    private static IReadOnlyList<StatementSyntax> EmitFixedMarkerCheck(UnionPlan union)
    {
        if (union.FixedMarker is not { } marker)
        {
            return [];
        }

        var presence = SyntaxFactory.IfStatement(
            SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("payload"), "TryGetProperty"),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(marker.WireName))),
                SyntaxFactory
                    .Argument(SyntaxFactory.DeclarationExpression(
                        SyntaxFactory.IdentifierName("var"),
                        SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("fixedElement"))))
                    .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)))),
            ThrowJson($"The {union.ConceptName} payload must contain '{marker.WireName}'."));
        var value = SyntaxFactory.IfStatement(
            EmitFixedMarkerMismatch(union, marker),
            ThrowJson($"The '{marker.WireName}' marker must be '{marker.Value}'."));
        return [presence, value];
    }

    private static BinaryExpressionSyntax EmitFixedMarkerMismatch(UnionPlan union, UnionFixedMarkerPlan marker) => marker.Kind switch
    {
        LiteralKind.String => SyntaxFactory.BinaryExpression(
            SyntaxKind.LogicalOrExpression,
            SyntaxFactory.BinaryExpression(
                SyntaxKind.NotEqualsExpression,
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("fixedElement"), "ValueKind"),
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonValueKind"), "String")),
            SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("fixedElement"), "ValueEquals"),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(marker.Value)))))),
        LiteralKind.Boolean when bool.TryParse(marker.Value, out var flag) => SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("fixedElement"), "ValueKind"),
            EmissionSyntax.MemberAccess(
                SyntaxFactory.IdentifierName("JsonValueKind"),
                flag ? "True" : "False")),
        LiteralKind.Number or _ => throw new InvalidOperationException(
            $"Union '{union.ConceptName}' fixes marker '{marker.WireName}' with kind '{marker.Kind}', which has no emission consumer."),
    };

    private static IfStatementSyntax EmitObjectPayloadCheck(UnionPlan union) =>
        SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.NotEqualsExpression,
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("payload"), "ValueKind"),
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonValueKind"), "Object")),
            ThrowJson($"The {union.ConceptName} payload must be a JSON object."));

    /// <summary>
    /// The carrier's own read reproduces the base converter's fallback arm, so it accepts the
    /// same marker property and preserves its value.
    /// </summary>
    private static IfStatementSyntax EmitMarkerPresenceCheck(UnionPlan union)
    {
        var missing = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("payload"), "TryGetProperty"),
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(union.MarkerWireName))),
            SyntaxFactory.Argument(SyntaxFactory.DeclarationExpression(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("markerElement"))))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword))));
        return SyntaxFactory.IfStatement(
            missing,
            ThrowJson($"The {union.ConceptName} payload must contain {MarkerDescription(union)}."));
    }

    private static IReadOnlyList<StatementSyntax> EmitMarkerRead(UnionPlan union) => union.MarkerKind switch
    {
        LiteralKind.String => EmitStringMarkerRead(union),
        LiteralKind.Boolean => EmitBooleanMarkerRead(union),
        LiteralKind.Number => throw new InvalidOperationException(
            $"Union '{union.ConceptName}' uses a number marker, which has no emission consumer."),
        _ => throw new InvalidOperationException($"Unknown marker kind '{union.MarkerKind}'."),
    };

    private static IReadOnlyList<StatementSyntax> EmitStringMarkerRead(UnionPlan union) =>
    [
        SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.NotEqualsExpression,
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("markerElement"), "ValueKind"),
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonValueKind"), "String")),
            ThrowJson($"The {MarkerDescription(union)} marker must be a string.")),
        EmissionSyntax.Local("marker", EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("markerElement"), "GetString"))),
        // An empty or whitespace marker is malformed input, classified before any dispatch so the
        // unknown-variant carrier's own guard can never surface as a BCL escape. The
        // explicit null disjunct carries the non-null flow fact on TFMs whose BCL lacks the
        // IsNullOrWhiteSpace annotation.
        SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalOrExpression,
                SyntaxFactory.IsPatternExpression(
                    SyntaxFactory.IdentifierName("marker"),
                    SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                        "IsNullOrWhiteSpace"),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("marker")))),
            ThrowJson($"The {MarkerDescription(union)} marker must be a non-empty string.")),
    ];

    private static IReadOnlyList<StatementSyntax> EmitBooleanMarkerRead(UnionPlan union)
    {
        var valueKind = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("markerElement"), "ValueKind");
        return
        [
            SyntaxFactory.IfStatement(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalAndExpression,
                    SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        valueKind,
                        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonValueKind"), "True")),
                    SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        valueKind,
                        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonValueKind"), "False"))),
                ThrowJson($"The '{union.MarkerWireName}' marker must be a boolean.")),
            EmissionSyntax.Local("marker", EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("markerElement"), "GetBoolean"))),
        ];
    }

    /// <summary>
    /// The probe that answers whether the payload's marker is a declared one: a string marker
    /// dispatches through the reader, which materializes the marker only on the unknown path.
    /// Any other kind has already read its marker.
    /// </summary>
    private static InvocationExpressionSyntax EmitDispatchProbe(UnionPlan union) =>
        union.MarkerKind is not LiteralKind.String
            ? EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("TypesByTag"), "TryGetValue"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("marker")),
                OutVariableArgument("targetType"))
            : EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("DiscriminatorReader"), "TryFindKnown"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader"))
                    .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
                RuntimeStringArgument(union.MarkerWireName),
                RuntimeStringArgument(union.ConceptName),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("TypesByTag")),
                OutVariableArgument("targetType"),
                OutVariableArgument("marker"));

    private static IfStatementSyntax EmitKnownDispatch(UnionPlan union)
    {
        var tryGet = EmitDispatchProbe(union);
        var getTypeInfo = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(
                RegistryNames.Default,
                "GetTypeInfo"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("targetType")));
        var typeInfo = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            getTypeInfo,
            SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException($"The generated context has no metadata for {union.ConceptName}.")));
        var deserialize = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonSerializer"), "Deserialize"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader"))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("typeInfo")));
        var result = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            SyntaxFactory.BinaryExpression(
                SyntaxKind.AsExpression,
                deserialize,
                TypeSyntaxEmitter.EmitNamed(union.Name)),
            SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException($"The {union.ConceptName} payload deserialized to null.")));
        var statements = new List<StatementSyntax>();
        if (union.KnownImpossibleTags.Count > 0)
        {
            statements.Add(SyntaxFactory.IfStatement(
                SyntaxFactory.IsPatternExpression(
                    SyntaxFactory.IdentifierName("targetType"),
                    SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                ThrowJson($"The {union.ConceptName} payload uses a declared marker whose schema admits no JSON value.")));
        }

        statements.Add(EmissionSyntax.Local("typeInfo", typeInfo));
        statements.Add(SyntaxFactory.ReturnStatement(result));
        return SyntaxFactory.IfStatement(tryGet, SyntaxFactory.Block(statements));
    }

    /// <summary>
    /// The prefix-tagged arm dispatches after every literal tag has missed and before the
    /// unknown carrier: <c>marker</c> is the string the known-dispatch probe materialized on
    /// its miss, and the arm reads through its own registered metadata.
    /// </summary>
    private static IReadOnlyList<StatementSyntax> EmitPrefixDispatch(UnionPlan union)
    {
        if (union.PrefixVariant is not { } prefix)
        {
            return [];
        }

        if (union.MarkerKind is not LiteralKind.String)
        {
            throw new InvalidOperationException(
                $"Union '{union.ConceptName}' carries a prefix-tagged arm on a '{union.MarkerKind}' marker, which has no emission consumer.");
        }

        var deserialize = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonSerializer"), "Deserialize"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader"))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            SyntaxFactory.Argument(EmissionSyntax.MemberAccess(
                RegistryNames.Default,
                prefix.TypeName)));
        var result = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            deserialize,
            SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException($"The {union.ConceptName} payload deserialized to null.")));
        return
        [
            SyntaxFactory.IfStatement(
                EmissionSyntax.StartsWithOrdinal(SyntaxFactory.IdentifierName("marker"), prefix.Prefix),
                SyntaxFactory.Block(SyntaxFactory.ReturnStatement(result))),
        ];
    }

    private static MethodDeclarationSyntax EmitWrite(UnionPlan union)
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

    private static ThrowStatementSyntax ThrowJson(string message) => SyntaxFactory.ThrowStatement(EmissionSyntax.JsonException(message));

}
