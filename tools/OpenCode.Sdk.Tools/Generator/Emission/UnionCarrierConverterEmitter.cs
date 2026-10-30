using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionCarrierConverterEmitter
{
    /// <summary>
    /// The carrier's own converter: reading reproduces the base converter's fallback arm,
    /// writing replays the preserved document.
    /// </summary>
    internal static GeneratedSource EmitCarrierConverter(UnionPlan union, ModelPlacement placement, GeneratedNamespacePlan namespaces)
    {
        var converterName = ConverterNamePolicy.Of(union.UnknownTypeName);
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
                ConverterSyntax.ThrowJson($"The {union.ConceptName} payload carries the '{prefix.Prefix}' prefix-tagged arm and is not an unknown {GeneratedDisplayName.Of(union.ConceptName)}.")),
        ];
    }

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
            ConverterSyntax.ThrowJson($"The {union.ConceptName} payload must contain '{marker.WireName}'."));
        var value = SyntaxFactory.IfStatement(
            EmitFixedMarkerMismatch(union, marker),
            ConverterSyntax.ThrowJson($"The '{marker.WireName}' marker must be '{marker.Value}'."));
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
            ConverterSyntax.ThrowJson($"The {union.ConceptName} payload must be a JSON object."));

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
            ConverterSyntax.ThrowJson($"The {union.ConceptName} payload must contain {UnionUnknownEmitter.MarkerDescription(union)}."));
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
            ConverterSyntax.ThrowJson($"The {UnionUnknownEmitter.MarkerDescription(union)} marker must be a string.")),
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
            ConverterSyntax.ThrowJson($"The {UnionUnknownEmitter.MarkerDescription(union)} marker must be a non-empty string.")),
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
                ConverterSyntax.ThrowJson($"The '{union.MarkerWireName}' marker must be a boolean.")),
            EmissionSyntax.Local("marker", EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("markerElement"), "GetBoolean"))),
        ];
    }
}
