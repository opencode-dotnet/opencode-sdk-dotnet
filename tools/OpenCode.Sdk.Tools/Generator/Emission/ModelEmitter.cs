using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class ModelEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var unions = plan.Unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);

        var result = new List<GeneratedSource>(plan.Models.Count);
        foreach (var model in plan.Models.OrderBy(static model => model.Name, StringComparer.Ordinal))
        {
            if (model is StructuralUnionModelPlan)
            {
                continue;
            }

            result.Add(model switch
            {
                ObjectModelPlan objectModel => EmitObject(objectModel, ResolveImplementedUnions(objectModel, unions), unions),
                EnumModelPlan enumModel => EmitEnum(enumModel),
                _ => throw new InvalidOperationException($"Unknown model plan '{model.GetType().Name}'."),
            });
        }

        return Array.AsReadOnly([.. result]);
    }

    private static GeneratedSource EmitObject(ObjectModelPlan model, IReadOnlyList<UnionPlan> implemented,
        IReadOnlyDictionary<string, UnionPlan> unions)
    {
        // A variant overrides one abstract marker per level of its union chain: a nested
        // union's variant carries both its own tag and the fixed outer tag. A union's one
        // prefix-tagged arm carries that union's marker as a guarded string in place of a tag.
        var chainMarkers = GetChainMarkers(model.Name, implemented, unions);
        var unmatched = chainMarkers
            .FirstOrDefault(marker => model.Properties.Count(property => IsDiscriminator(property, marker)) is not 1);
        if (unmatched is not null)
        {
            throw new InvalidOperationException($"Union variant '{model.Name}' must carry exactly one '{unmatched.WireName}' marker.");
        }

        var declaration = SyntaxFactory
            .RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), model.Name)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
            .WithMembers(SyntaxFactory.List(
            [
                .. model.Properties.Select(property => EmitProperty(property, CarriedMarker(chainMarkers, property))),
                .. model.RequestQueryProperties.Select(static property => EmitRequestQueryProperty(property)),
                .. model.ExplicitHoistedImplementations.Select(static implementation => EmitHoistedImplementation(implementation)),
                .. EmitExtensionDataMembers(model),
                .. EmitRedactingToString(model, chainMarkers),
            ]))
            .WithLeadingTrivia(EmissionSyntax.Documentation(model.Description ?? $"Represents a {GeneratedDisplayName.Of(model.Name)} value."));
        // A hoisted carrier joins the base list beside the unions the schema is a branch of: the
        // record keeps one identity per wire schema and answers one more contract (ADR-0011).
        var baseTypes = model.ImplementedUnionNames.Concat(model.ImplementedHoistedInterfaceNames).ToArray();
        if (baseTypes.Length > 0)
        {
            declaration = declaration.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SeparatedList<BaseTypeSyntax>(
            [
                .. baseTypes.Select(static name => SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.EmitNamed(name))),
            ])));
        }

        var guardsPrefix = chainMarkers.Any(static marker => marker.Prefix is not null);
        var unit = EmissionSyntax.CompilationUnit(model.Namespace, CollectUsings(model, guardsPrefix), [declaration]);
        return EmissionSyntax.CreateSource($"Models/{model.Name}.cs", unit);
    }

    private static GeneratedSource EmitEnum(EnumModelPlan model)
    {
        // Strict by contract: the schema types enum values as strings, so the permissive
        // default's integer tolerance would admit malformed bodies as undefined members.
        var converterType = TypeSyntaxEmitter.Generic("StrictJsonStringEnumConverter", TypeSyntaxEmitter.EmitNamed(model.Name));
        var declaration = SyntaxFactory
            .EnumDeclaration(model.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonConverter", SyntaxFactory.AttributeArgument(
                SyntaxFactory.TypeOfExpression(converterType))))
            .WithMembers(SyntaxFactory.SeparatedList(model.Values.Select(static value => SyntaxFactory
                .EnumMemberDeclaration(value.Name)
                .AddAttributeLists(EmissionSyntax.Attribute("JsonStringEnumMemberName", EmissionSyntax.StringArgument(value.WireValue)))
                .WithLeadingTrivia(EmissionSyntax.Documentation($"Represents the '{value.WireValue}' wire value.")))))
            .WithLeadingTrivia(EmissionSyntax.Documentation(model.Description ?? $"Defines the supported {GeneratedDisplayName.Of(model.Name)} values."));
        var unit = EmissionSyntax.CompilationUnit(
            model.Namespace,
            ["System.Text.Json.Serialization", "OpenCode.Sdk.Internal.Serialization"],
            [declaration]);
        return EmissionSyntax.CreateSource($"Models/{model.Name}.cs", unit);
    }

    /// <summary>
    /// Answers one hoisted interface member the record's own property cannot satisfy on its own —
    /// a value type against a nullable member, or a promoted record against its hoisted carrier.
    /// An explicit implementation is not a public property, so System.Text.Json ignores it and the
    /// serialized shape is exactly what it was before the member was hoisted.
    /// </summary>
    private static PropertyDeclarationSyntax EmitHoistedImplementation(HoistedImplementationPlan implementation)
    {
        ExpressionSyntax own = SyntaxFactory.IdentifierName(implementation.PropertyName);
        if (implementation.ReadsOptionalValue)
        {
            own = EmissionSyntax.MemberAccess(own, "Value");
        }

        return SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.Emit(implementation.MemberType), implementation.MemberName)
            .WithExplicitInterfaceSpecifier(SyntaxFactory.ExplicitInterfaceSpecifier(
                SyntaxFactory.IdentifierName(implementation.InterfaceName)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(own))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>A merged request's query-side property never serializes; the route builder reads it.</summary>
    private static PropertyDeclarationSyntax EmitRequestQueryProperty(QueryPropertyPlan property)
    {
        // The documentation trivia must stay ahead of the added attribute list.
        var declaration = QueryRequestEmitter.EmitProperty(property);
        var documentation = declaration.GetLeadingTrivia();
        return declaration
            .WithoutLeadingTrivia()
            .AddAttributeLists(EmissionSyntax.Attribute("JsonIgnore"))
            .WithLeadingTrivia(documentation);
    }

    /// <summary>
    /// A discriminator is emitted under the marker member its union promises rather than under
    /// the property's own derived name, so a variant tagged by a second dialect's wire property
    /// still satisfies the one interface member. The wire name it serializes as is untouched.
    /// A literal tag is a constant; the prefix-tagged arm's marker is a required string whose
    /// initializer refuses any value outside the prefix.
    /// </summary>
    private static PropertyDeclarationSyntax EmitProperty(ModelPropertyPlan property, ChainMarker? marker)
    {
        var memberName = marker?.MemberName ?? property.Name;
        var propertyType = property.EmitsOptionalWrapper
            ? TypeSyntaxEmitter.Generic("Optional", TypeSyntaxEmitter.Emit(property.Type))
            : TypeSyntaxEmitter.Emit(property.Type);
        var declaration = SyntaxFactory
            .PropertyDeclaration(propertyType, memberName)
            .AddAttributeLists(EmissionSyntax.Attribute("JsonPropertyName", EmissionSyntax.StringArgument(property.WireName)))
            .WithLeadingTrivia(EmissionSyntax.Documentation(property.Description ?? $"Gets the {GeneratedDisplayName.Of(memberName)} value."));
        if (ContainsSpecialNumber(property.Type))
        {
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonNumberHandling",
                SyntaxFactory.AttributeArgument(EmissionSyntax.MemberAccess(
                    SyntaxFactory.IdentifierName("JsonNumberHandling"),
                    "AllowNamedFloatingPointLiterals"))));
        }

        if (!property.IsRequired)
        {
            // The wrapper's absent state is default, and WhenWritingNull is invalid on a
            // non-nullable value type: omission is decided by the struct, not by CLR null.
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonIgnore",
                SyntaxFactory
                    .AttributeArgument(EmissionSyntax.MemberAccess(
                        SyntaxFactory.IdentifierName("JsonIgnoreCondition"),
                        property.EmitsOptionalWrapper ? "WhenWritingDefault" : "WhenWritingNull"))
                    .WithNameEquals(SyntaxFactory.NameEquals("Condition"))));
        }

        if (property.EmitsOptionalWrapper)
        {
            // Member-level rather than type-level: the attribute argument cannot name an
            // unbound generic, so each instantiation points at its own closed converter.
            declaration = declaration.AddAttributeLists(EmissionSyntax.Attribute(
                "JsonConverter",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(
                    TypeSyntaxEmitter.EmitNamed(OptionalConverterNamePolicy.ConverterTypeName(property.Type))))));
        }

        if (marker?.Prefix is { } prefix)
        {
            if (!property.IsRequired)
            {
                throw new InvalidOperationException($"Property '{property.Name}' carries a prefix-tagged marker but is not required.");
            }

            return declaration
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.RequiredKeyword)))
                .WithAccessorList(EmitPrefixGuardedAccessors(property.WireName, prefix));
        }

        if (marker is not null)
        {
            return declaration
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmitLiteral(property)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
        };
        if (property.IsRequired)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.RequiredKeyword));
        }

        declaration = declaration
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithAccessorList(EmitAutoAccessors());
        return declaration;
    }

    /// <summary>
    /// The two members every open model shares. The public one is the read-only, init-only
    /// view every other dictionary member of the model layer has (ADR-0004); the serializer
    /// ignores it. The internal one is what System.Text.Json fills: it routes each wire member
    /// no named property claims into the bag by wire name and writes the bag back on
    /// serialization, so nothing the server sent is lost. It is a plain settable property
    /// because the serializer deserializes only members with a setter
    /// (<c>JsonPropertyInfo.CanDeserialize = HasSetter</c>) and binds an init-only member
    /// through the constructor path, which extension data may not take
    /// (<c>ExtensionDataCannotBindToCtorParam</c>); <c>JsonInclude</c> admits the non-public
    /// member and the internal accessibility keeps it off the public surface. The bag starts
    /// null and the serializer creates it on the first open member, so a body without one
    /// allocates nothing; until then the view answers the shared empty instance.
    /// </summary>
    private static IEnumerable<MemberDeclarationSyntax> EmitExtensionDataMembers(ObjectModelPlan model)
    {
        if (!model.EmitsExtensionData)
        {
            yield break;
        }

        var stringType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword));
        var elementType = TypeSyntaxEmitter.EmitNamed("JsonElement");
        var ordinal = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StringComparer"), "Ordinal");
        var bagName = SyntaxFactory.IdentifierName(ObjectModelPlan.ExtensionDataBagName);

        // init => OpenMembers = value.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
        var copy = SyntaxFactory.InvocationExpression(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "ToDictionary"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(PairSelector("Key")),
                SyntaxFactory.Argument(PairSelector("Value")),
                SyntaxFactory.Argument(ordinal),
            ])));
        var store = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, bagName, copy));
        // get => OpenMembers ?? EmptyOpenMembers.Instance
        var view = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            bagName,
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("EmptyOpenMembers"), "Instance"));
        yield return SyntaxFactory
            .PropertyDeclaration(
                TypeSyntaxEmitter.Generic("IReadOnlyDictionary", stringType, elementType),
                ObjectModelPlan.ExtensionDataMemberName)
            .AddAttributeLists(EmissionSyntax.Attribute("JsonIgnore"))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(
            [
                SyntaxFactory
                    .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(view))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory
                    .AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithBody(SyntaxFactory.Block(SyntaxFactory.List([.. EmissionSyntax.ArgumentNullGuard("value"), store]))),
            ])))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                "Gets the members the pinned document leaves open, keyed by wire name; empty when the body carried none."));

        yield return SyntaxFactory
            .PropertyDeclaration(
                SyntaxFactory.NullableType(TypeSyntaxEmitter.Generic("Dictionary", stringType, elementType)),
                ObjectModelPlan.ExtensionDataBagName)
            .AddAttributeLists(EmissionSyntax.Attribute("JsonExtensionData"))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonInclude"))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(
            [
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            ])))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                $"Gets or sets the serializer's writable bag behind {ObjectModelPlan.ExtensionDataMemberName}, created on the first open member; the public member is the read-only view."));
    }

    /// <summary>
    /// A model with a secret member prints itself instead of taking the compiler's ToString
    /// (ADR-0028): the same shape and the same members in declaration order, the secret ones
    /// through <c>RecordPrinter.Redact</c>. It overrides ToString rather than declaring
    /// PrintMembers, which a sealed record with no record base may only declare private (CS8879),
    /// where IDE0051 cannot see the synthesized caller and the writer's format pass would strip it.
    /// </summary>
    private static IEnumerable<MemberDeclarationSyntax> EmitRedactingToString(ObjectModelPlan model, IReadOnlyList<ChainMarker> markers)
    {
        if (!model.Properties.Any(static property => property.IsRedacted))
        {
            yield break;
        }

        var members = model.Properties
            .Select(property => PrintedMember(property, CarriedMarker(markers, property)?.MemberName ?? property.Name))
            .Concat(model.RequestQueryProperties.Select(static property => RecordPrinterSyntax.Member(property.PropertyName)))
            .Concat(model.EmitsExtensionData ? [RecordPrinterSyntax.Member(ObjectModelPlan.ExtensionDataMemberName)] : []);
        yield return RecordPrinterSyntax.ToStringOverride(
            RecordPrinterSyntax.Format(model.Name, members),
            "Prints the record's members with its secret members masked.");
    }

    /// <summary><c>("Name", Name)</c>, or <c>("Name", RecordPrinter.Redact(Name))</c> for a secret member; an unset tri-state member prints empty.</summary>
    private static ArgumentSyntax PrintedMember(ModelPropertyPlan property, string memberName)
    {
        if (!property.IsRedacted)
        {
            return RecordPrinterSyntax.Member(memberName);
        }

        var member = SyntaxFactory.IdentifierName(memberName);
        ExpressionSyntax masked = property.EmitsOptionalWrapper
            ? SyntaxFactory.ConditionalExpression(
                EmissionSyntax.MemberAccess(member, "IsSet"),
                RecordPrinterSyntax.Redact(EmissionSyntax.MemberAccess(member, "Value")),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
            : RecordPrinterSyntax.Redact(member);
        return RecordPrinterSyntax.Member(memberName, masked);
    }

    /// <summary><c>static pair => pair.&lt;member&gt;</c> for the copy the public init accessor takes.</summary>
    private static SimpleLambdaExpressionSyntax PairSelector(string member) =>
        SyntaxFactory
            .SimpleLambdaExpression(SyntaxFactory.Parameter(SyntaxFactory.Identifier("pair")))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithExpressionBody(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("pair"), member));

    private static AccessorListSyntax EmitAutoAccessors() => SyntaxFactory.AccessorList(SyntaxFactory.List(
    [
        SyntaxFactory
            .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
        SyntaxFactory
            .AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
    ]));

    /// <summary>
    /// <c>get; init { … }</c> for the prefix-tagged arm's marker. The initializer refuses null
    /// and any value outside the prefix — the typed twin of the unknown carrier refusing a
    /// marker the prefix claims — and stores through the <c>field</c> keyword.
    /// </summary>
    private static AccessorListSyntax EmitPrefixGuardedAccessors(string wireName, string prefix)
    {
        var refusal = SyntaxFactory.IfStatement(
            SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                EmissionSyntax.StartsWithOrdinal(SyntaxFactory.IdentifierName("value"), prefix)),
            SyntaxFactory.Block(EmissionSyntax.ThrowArgumentException(
                $"The '{wireName}' marker must carry the '{prefix}' prefix.",
                "value")));
        var store = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.FieldExpression(),
            SyntaxFactory.IdentifierName("value")));
        return SyntaxFactory.AccessorList(SyntaxFactory.List(
        [
            SyntaxFactory
                .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory
                .AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(SyntaxFactory.List(
                [
                    .. EmissionSyntax.ArgumentNullGuard("value"),
                    refusal,
                    store,
                ]))),
        ]));
    }

    private static LiteralExpressionSyntax EmitLiteral(ModelPropertyPlan property) => property.LiteralKind switch
    {
        LiteralKind.String => SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(property.LiteralValue ?? throw new InvalidOperationException("String literal had no value."))),
        LiteralKind.Boolean when bool.TryParse(property.LiteralValue, out var value) => SyntaxFactory.LiteralExpression(
            value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
        LiteralKind.Number => throw new InvalidOperationException(
            $"Property '{property.Name}' uses a number literal, which has no emission consumer."),
        _ => throw new InvalidOperationException($"Property '{property.Name}' has an invalid literal plan."),
    };

    private static IReadOnlyList<string> CollectUsings(ObjectModelPlan model, bool guardsPrefix)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (model.Properties.Count > 0)
        {
            _ = result.Add("System.Text.Json.Serialization");
        }

        foreach (var implementation in model.ExplicitHoistedImplementations)
        {
            TypeUsingCollector.Collect(implementation.MemberType, result);
        }

        // The guarded marker's initializer throws BCL argument exceptions and compares ordinally.
        if (guardsPrefix)
        {
            _ = result.Add("System");
        }

        // A tri-state member names its closed converter, which lives with the runtime's own
        // serialization internals. Optional<T> itself sits in the root namespace this one
        // nests under, so it needs no using.
        if (model.Properties.Any(static property => property.EmitsOptionalWrapper))
        {
            _ = result.Add("OpenCode.Sdk.Internal.Serialization");
        }

        // A model with a secret member prints through the runtime's record printer.
        if (model.Properties.Any(static property => property.IsRedacted))
        {
            _ = result.Add("OpenCode.Sdk.Internal.Serialization");
        }

        // The extension-data members name the dictionary types, JsonElement, the ordinal
        // comparer, the null guard, the LINQ copy the init accessor takes, and the shared
        // empty view the runtime keeps for a bag the serializer has not created.
        if (model.EmitsExtensionData)
        {
            _ = result.Add("OpenCode.Sdk.Internal.Serialization");
            _ = result.Add("System");
            _ = result.Add("System.Collections.Generic");
            _ = result.Add("System.Linq");
            _ = result.Add("System.Text.Json");
            _ = result.Add("System.Text.Json.Serialization");
        }

        foreach (var property in model.Properties)
        {
            TypeUsingCollector.Collect(property.Type, result);
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }

    private static bool ContainsSpecialNumber(TypeReferencePlan type) => type switch
    {
        SpecialNumberTypeReferencePlan => true,
        ListTypeReferencePlan list => ContainsSpecialNumber(list.ElementType),
        DictionaryTypeReferencePlan dictionary => ContainsSpecialNumber(dictionary.ValueType),
        _ => false,
    };

    private static List<UnionPlan> ResolveImplementedUnions(ObjectModelPlan model, Dictionary<string, UnionPlan> unions) =>
    [
        .. model.ImplementedUnionNames.Select(name => unions.TryGetValue(name, out var union)
            ? union
            : throw new InvalidOperationException($"Model '{model.Name}' references absent union '{name}'.")),
    ];

    /// <summary>
    /// A variant carries a chain marker as the literal its tag fixes or, as the union's one
    /// prefix-tagged arm, as the plain string the prefix claims — never as both.
    /// </summary>
    private static bool IsDiscriminator(ModelPropertyPlan property, ChainMarker marker) =>
        string.Equals(property.WireName, marker.WireName, StringComparison.Ordinal)
        && (marker.Prefix is null ? property.IsLiteral : !property.IsLiteral);

    private static ChainMarker? CarriedMarker(IEnumerable<ChainMarker> markers, ModelPropertyPlan property) =>
        markers.FirstOrDefault(marker => IsDiscriminator(property, marker));

    /// <summary>
    /// The marker each union in the schema's membership expects it to carry, walking every
    /// chain, paired with the member name that union promises for it and, when the schema is
    /// that union's prefix-tagged arm, the prefix its value must carry. Two unions may name
    /// different markers, and the schema then carries both; the same name is one property
    /// serving both contracts. A union that dispatches on more than one wire property reads
    /// this schema's own variant entry, so each variant answers under the union's one member.
    /// </summary>
    private static List<ChainMarker> GetChainMarkers(string modelName,
        IReadOnlyList<UnionPlan> implemented, IReadOnlyDictionary<string, UnionPlan> unions)
    {
        var result = new List<ChainMarker>();
        foreach (var union in implemented)
        {
            var current = union;
            while (current is not null)
            {
                var wireName = VariantMarkerWireName(current, modelName);
                if (!result.Any(entry => string.Equals(entry.WireName, wireName, StringComparison.Ordinal)))
                {
                    result.Add(new ChainMarker(wireName, current.MarkerName, PrefixOf(current, modelName, wireName)));
                }

                current = current.BaseTypeName is not null && unions.TryGetValue(current.BaseTypeName, out var outer)
                    ? outer
                    : null;
            }
        }

        return result;
    }

    private static string VariantMarkerWireName(UnionPlan union, string modelName) =>
        union
            .Variants.FirstOrDefault(variant => string.Equals(variant.TypeName, modelName, StringComparison.Ordinal))
            ?.MarkerWireName
        ?? union.MarkerWireName;

    /// <summary>The prefix a union's prefix-tagged arm must carry on its marker; null for every other variant.</summary>
    private static string? PrefixOf(UnionPlan union, string modelName, string wireName) =>
        union.PrefixVariant is { } prefix
        && string.Equals(prefix.TypeName, modelName, StringComparison.Ordinal)
        && string.Equals(prefix.MarkerWireName, wireName, StringComparison.Ordinal)
            ? prefix.Prefix
            : null;

    /// <summary>
    /// One marker a union in the schema's chain expects it to carry: the wire property, the
    /// member the union promises for it, and — for the union's prefix-tagged arm — the prefix
    /// the member's value must carry in place of a literal tag.
    /// </summary>
    private sealed record ChainMarker(string WireName, string MemberName, string? Prefix);
}
