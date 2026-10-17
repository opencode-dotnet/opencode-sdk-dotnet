using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits one adapter per streaming operation: the payload metadata each frame is read through,
/// the event name that reports a mid-stream failure, and the declared error tags a status
/// refused before the stream opens maps onto.
/// </summary>
internal static class StreamAdapterEmitter
{
    private const string AdapterNamespace = GeneratedNamespace.Internal;

    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ClientPlan> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);

        return Array.AsReadOnly(
        [
            .. clients
                .SelectMany(static client => client.Operations)
                .Where(static operation => operation.Stream is not null)
                .OrderBy(static operation => operation.Stream!.AdapterTypeName, StringComparer.Ordinal)
                .Select(static operation => EmitAdapter(operation)),
        ]);
    }

    private static GeneratedSource EmitAdapter(OperationPlan operation)
    {
        var stream = operation.Stream!;
        var members = new List<MemberDeclarationSyntax>();
        members.AddRange(operation.ErrorMap.Statuses.Select(static status => EmitTagSet(status)));
        members.Add(SyntaxFactory
            .ConstructorDeclaration(stream.AdapterTypeName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithBody(SyntaxFactory.Block()));
        members.Add(EmitInstance(stream.AdapterTypeName));

        // A stream's declared success is the pinned 200 the SSE wall accepts; there is no
        // no-content arm because a success without a body cannot be framed.
        members.Add(StatusVerdictEmitter.EmitClassify(
            successStatus: 200,
            noContentSuccess: false,
            operation.ErrorMap.Statuses.Select(static status => status.StatusCode),
            overrides: false));
        members.Add(EmitFailureEventName(stream));
        members.Add(EmitPayloadTypeInfo(stream));
        members.Add(EmitCauseTypeInfo(stream));
        members.Add(EmitReadError(operation));

        var declaration = SyntaxFactory
            .ClassDeclaration(stream.AdapterTypeName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic(
                    "IStreamAdapter",
                    TypeSyntaxEmitter.EmitNamed(stream.PayloadTypeName),
                    TypeSyntaxEmitter.EmitNamed(stream.CauseTypeName))))))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                $"Carries the '{operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}' stream contract."));

        var unit = EmissionSyntax.CompilationUnit(
            AdapterNamespace,
            ["System", "System.Text.Json.Serialization.Metadata", GeneratedNamespace.Internal, GeneratedNamespace.Models],
            [declaration]);
        return EmissionSyntax.CreateSource(
            GeneratedPlacement.PathOf(GeneratedKind.StreamAdapter, TypeHome.Family(operation.RouteContainerName), stream.AdapterTypeName),
            unit);
    }

    private static FieldDeclarationSyntax EmitTagSet(ErrorStatusPlan status) =>
        SyntaxFactory
            .FieldDeclaration(SyntaxFactory
                .VariableDeclaration(
                    SyntaxFactory
                        .ArrayType(TypeSyntaxEmitter.EmitNamed("string"))
                        .WithRankSpecifiers(SyntaxFactory.SingletonList(
                            SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                SyntaxFactory.OmittedArraySizeExpression())))))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory
                        .VariableDeclarator(StatusTagsName(status.StatusCode))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>(
                            [
                                .. status.Tags.Select(static tag =>
                                    SyntaxFactory.ExpressionElement(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal(tag.Tag)))),
                            ])))))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static PropertyDeclarationSyntax EmitInstance(string adapterTypeName) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitNamed(adapterTypeName), "Instance")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory
                    .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
            .WithInitializer(SyntaxFactory.EqualsValueClause(
                SyntaxFactory
                    .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(adapterTypeName))
                    .WithArgumentList(SyntaxFactory.ArgumentList())))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation("Gets the shared adapter instance."));

    private static PropertyDeclarationSyntax EmitFailureEventName(StreamPlan stream) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitNamed("string"), "FailureEventName")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(stream.FailureEventName))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation("Gets the event name a mid-stream failure frame carries."));

    private static PropertyDeclarationSyntax EmitPayloadTypeInfo(StreamPlan stream) =>
        SyntaxFactory
            .PropertyDeclaration(
                TypeSyntaxEmitter.Generic("JsonTypeInfo", TypeSyntaxEmitter.EmitNamed(stream.PayloadTypeName)),
                "PayloadTypeInfo")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.MemberAccess(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("OpenCodeJsonContext"), "Default"),
                stream.PayloadTypeName)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation("Gets the metadata each frame's payload is read through."));

    private static PropertyDeclarationSyntax EmitCauseTypeInfo(StreamPlan stream)
    {
        var causeType = TypeSyntaxEmitter.EmitNamed(stream.CauseTypeName);
        var metadataType = TypeSyntaxEmitter.Generic("JsonTypeInfo", causeType);
        var getTypeInfo = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("OpenCodeJsonContext"), "Default"),
                "GetTypeInfo"),
            SyntaxFactory.Argument(SyntaxFactory.TypeOfExpression(causeType)));
        var value = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            SyntaxFactory.BinaryExpression(SyntaxKind.AsExpression, getTypeInfo, metadataType),
            SyntaxFactory.ThrowExpression(SyntaxFactory
                .ObjectCreationExpression(SyntaxFactory.IdentifierName("InvalidOperationException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal("The generated context has no metadata for the stream failure cause."))))))));
        return SyntaxFactory
            .PropertyDeclaration(metadataType, "CauseTypeInfo")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(value))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation("Gets the metadata the failure frame's cause is read through."));
    }

    private static MethodDeclarationSyntax EmitReadError(OperationPlan operation)
    {
        var arms = new List<SwitchExpressionArmSyntax>(
        [
            .. operation.ErrorMap.Statuses.Select(static status => SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(status.StatusCode))),
                EmitRead(SyntaxFactory.IdentifierName(StatusTagsName(status.StatusCode))))),
            SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.DiscardPattern(),
                EmitRead(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
        ]);

        return SyntaxFactory
            .MethodDeclaration(
                SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed(ErrorMarkerPolicy.InterfaceTypeName)),
                "ReadError")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("status")).WithType(TypeSyntaxEmitter.EmitNamed("int")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("rawBody")).WithType(TypeSyntaxEmitter.EmitNamed("string")),
            ])))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory
                    .SwitchExpression(SyntaxFactory.IdentifierName("status"))
                    .WithArms(SyntaxFactory.SeparatedList(arms))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                "Maps a status refused before the stream opened onto its declared tags."));
    }

    private static string StatusTagsName(int statusCode) =>
        string.Create(CultureInfo.InvariantCulture, $"Status{statusCode}Tags");

    private static InvocationExpressionSyntax EmitRead(ExpressionSyntax tags) => EmissionSyntax.Invocation(
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("OpenCodeErrorReader"), "Read"),
        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("rawBody")),
        SyntaxFactory.Argument(tags));
}
