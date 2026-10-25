using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionDispatchEmitter
{
    internal static GeneratedSource EmitConverter(UnionPlan union, ModelPlacement placement, GeneratedNamespacePlan namespaces)
    {
        var converterName = $"{union.ConceptName}JsonConverter";
        var declaration = ConverterSyntax.Declaration(converterName, TypeSyntaxEmitter.EmitNamed(union.Name),
            [
                .. EmitDispatchMaps(union),
                EmitDiscriminatorReader(),
                EmitRead(union),
                UnionDispatchWriteEmitter.EmitWrite(union),
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
                ConverterSyntax.ThrowJson($"The {union.ConceptName} payload uses a declared marker whose schema admits no JSON value.")));
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
}
