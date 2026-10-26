using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class ResponseAdapterEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ClientPlan> clients, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(namespaces);

        return Array.AsReadOnly(
        [
            .. EnvelopeOperations.Naming(clients, static envelope => envelope.AdapterTypeName)
                .Select(operation => EmitAdapter(operation, namespaces)),
        ]);
    }

    private static GeneratedSource EmitAdapter(OperationPlan operation, GeneratedNamespacePlan namespaces)
    {
        var envelope = operation.Envelope!;
        var members = new List<MemberDeclarationSyntax>();
        members.AddRange(operation.ErrorMap.Statuses.Select(static status => EmitTagSet(status)));
        members.Add(SyntaxFactory.ConstructorDeclaration(envelope.AdapterTypeName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithBody(SyntaxFactory.Block()));
        members.Add(EmitInstance(envelope.AdapterTypeName));
        members.Add(StatusVerdictEmitter.EmitClassify(
            envelope.SuccessStatusCode,
            noContentSuccess: envelope.Kind is EnvelopeKind.NoContent,
            operation.ErrorMap.Statuses.Select(static status => status.StatusCode),
            overrides: true));
        members.Add(EmitAdaptSuccess(envelope));
        members.Add(EmitAdapt(operation));
        if (operation.Pagination is not null)
        {
            members.Add(EmitGetItems(operation));
            members.Add(EmitGetNextCursor(operation));
            members.Add(EmitCreateNextRequest(operation));
        }

        if (envelope.Kind is EnvelopeKind.CursorList)
        {
            members.Add(EmitProjectingSuccessHelper(envelope, "Cursor"));
        }

        if (envelope.Kind is EnvelopeKind.DataLocation or EnvelopeKind.DataLocationList)
        {
            members.Add(EmitProjectingSuccessHelper(envelope, "Location"));
        }

        if (envelope.Kind is EnvelopeKind.Binary)
        {
            members.Add(EmitBinarySuccess());
            members.Add(EmitAdaptBinary(envelope));
        }

        var baseTypes = new List<BaseTypeSyntax>
        {
            SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic(
                "ResponseAdapter",
                TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName))),
        };
        if (operation.Pagination is { } pagination)
        {
            baseTypes.Add(SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic(
                "ICursorPageAdapter",
                TypeSyntaxEmitter.EmitNamed(pagination.RequestTypeName),
                TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName),
                TypeSyntaxEmitter.EmitNamed(pagination.ItemTypeName))));
        }

        var declaration = SyntaxFactory.ClassDeclaration(envelope.AdapterTypeName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(baseTypes)))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                $"Adapts the '{operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}' responses onto '{envelope.ResponseTypeName}'."));
        var usings = new List<string> { "System" };
        if (operation.Pagination is not null)
        {
            usings.Add("System.Collections.Generic");
            usings.Add(namespaces.Models);
        }

        var unit = EmissionSyntax.CompilationUnit(
            namespaces.Internal,
            usings,
            [declaration]);
        return EmissionSyntax.CreateSource(
            GeneratedPlacement.PathOf(GeneratedKind.ResponseAdapter, TypeHome.Family(operation.RouteContainerName), envelope.AdapterTypeName),
            unit);
    }

    private static FieldDeclarationSyntax EmitTagSet(ErrorStatusPlan status)
    {
        var tags = status.Tags.Select(static tag => (ExpressionSyntax)SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(tag.Tag)));
        return SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ArrayType(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                    SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier(
                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.OmittedArraySizeExpression())))),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(TagSetName(status.StatusCode))
                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.CollectionExpression(
                        SyntaxFactory.SeparatedList(tags.Select(SyntaxFactory.ExpressionElement)
                            .Cast<CollectionElementSyntax>())))))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));
    }

    private static PropertyDeclarationSyntax EmitInstance(string adapterTypeName) =>
        SyntaxFactory.PropertyDeclaration(TypeSyntaxEmitter.EmitNamed(adapterTypeName), "Instance")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
            .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(
                    TypeSyntaxEmitter.EmitNamed(adapterTypeName))
                .WithArgumentList(SyntaxFactory.ArgumentList())))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Gets the shared adapter instance."));

    private static MethodDeclarationSyntax EmitAdaptSuccess(EnvelopePlan envelope) =>
        SyntaxFactory.MethodDeclaration(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName), "AdaptSuccess")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("status"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("utf8Body"))
                    .WithType(TypeSyntaxEmitter.Generic(
                        "ReadOnlySpan",
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword)))),
            ])))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                EmitSuccessCreation(envelope, SyntaxFactory.IdentifierName("utf8Body"))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                envelope.Kind is EnvelopeKind.NoContent
                    ? "Creates the typed envelope for a success that carries no body."
                    : "Maps the declared UTF-8 success body onto the typed envelope."));

    private static PropertyDeclarationSyntax EmitBinarySuccess() =>
        SyntaxFactory.PropertyDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "BinarySuccess")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Gets a value indicating that the success body is raw bytes."));

    /// <summary>
    /// The envelope of a raw-byte success: the bytes are copied out of the pooled buffer, which the
    /// pipeline returns when the message ends, beside the server's <c>Content-Type</c>.
    /// </summary>
    private static MethodDeclarationSyntax EmitAdaptBinary(EnvelopePlan envelope) =>
        SyntaxFactory.MethodDeclaration(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName), "AdaptBinary")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("status"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("body"))
                    .WithType(TypeSyntaxEmitter.Generic(
                        "ReadOnlySpan",
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword)))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("contentType"))
                    .WithType(SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))),
            ])))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory
                .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName))
                .WithArgumentList(SyntaxFactory.ArgumentList())
                .WithInitializer(SyntaxFactory.InitializerExpression(
                    SyntaxKind.ObjectInitializerExpression,
                    SyntaxFactory.SeparatedList<ExpressionSyntax>(
                    [
                        Initialize("Status", SyntaxFactory.IdentifierName("status")),
                        Initialize(RequirePayloadName(envelope), EmissionSyntax.Invocation(
                            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("body"), "ToArray"))),
                        Initialize("ContentType", SyntaxFactory.IdentifierName("contentType")),
                    ])))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Maps the raw success bytes and their content type onto the typed envelope."));

    private static AssignmentExpressionSyntax Initialize(string member, ExpressionSyntax value) =>
        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(member), value);

    private static MethodDeclarationSyntax EmitAdapt(OperationPlan operation)
    {
        var envelope = operation.Envelope!;
        var arms = new List<SwitchExpressionArmSyntax>
        {
            SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.ConstantPattern(Number(envelope.SuccessStatusCode)),
                EmitSuccessCreation(envelope, SyntaxFactory.IdentifierName("rawBody"))),
            // Any other 2xx is outside the declared contract: a protocol failure, never an API error.
            SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.BinaryPattern(
                    SyntaxKind.AndPattern,
                    SyntaxFactory.RelationalPattern(SyntaxFactory.Token(SyntaxKind.GreaterThanEqualsToken), Number(200)),
                    SyntaxFactory.RelationalPattern(SyntaxFactory.Token(SyntaxKind.LessThanToken), Number(300))),
                SyntaxFactory.ThrowExpression(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StatusVerdictFailures"), "UndeclaredSuccess"),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("status"))))),
        };
        arms.AddRange(operation.ErrorMap.Statuses.Select(status => SyntaxFactory.SwitchExpressionArm(
            SyntaxFactory.ConstantPattern(Number(status.StatusCode)),
            EmitErrorCreation(envelope, SyntaxFactory.IdentifierName(TagSetName(status.StatusCode))))));
        arms.Add(SyntaxFactory.SwitchExpressionArm(
            SyntaxFactory.DiscardPattern(),
            EmitErrorCreation(envelope, SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.ArgumentNullGuard("rawBody"));
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.SwitchExpression(
            SyntaxFactory.IdentifierName("status"),
            SyntaxFactory.SeparatedList(arms))));
        return SyntaxFactory.MethodDeclaration(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName), "Adapt")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("status"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("rawBody"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
            ])))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Maps one buffered response onto the typed envelope."));
    }

    private static MethodDeclarationSyntax EmitGetItems(OperationPlan operation)
    {
        var pagination = operation.Pagination!;
        var responseTypeName = operation.Envelope!.ResponseTypeName;
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.ArgumentNullGuard("response"));
        statements.Add(SyntaxFactory.ReturnStatement(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("response"), pagination.PayloadName)));
        return SyntaxFactory.MethodDeclaration(
                TypeSyntaxEmitter.Generic("IReadOnlyList", TypeSyntaxEmitter.EmitNamed(pagination.ItemTypeName)),
                "GetItems")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("response"))
                    .WithType(TypeSyntaxEmitter.EmitNamed(responseTypeName)))))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Gets the ordered items carried by one successful page."));
    }

    private static MethodDeclarationSyntax EmitGetNextCursor(OperationPlan operation)
    {
        var responseTypeName = operation.Envelope!.ResponseTypeName;
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.ArgumentNullGuard("response"));
        statements.Add(SyntaxFactory.ReturnStatement(EmissionSyntax.MemberAccess(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("response"), "Cursor"),
            "Next")));
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
                "GetNextCursor")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("response"))
                    .WithType(TypeSyntaxEmitter.EmitNamed(responseTypeName)))))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Gets the opaque next cursor carried by one successful page."));
    }

    private static MethodDeclarationSyntax EmitCreateNextRequest(OperationPlan operation)
    {
        var requestTypeName = operation.Pagination!.RequestTypeName;
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.ArgumentNullGuard("cursor"));

        // The whole first request rides the continuation, so a filter added to the query needs no
        // emitter change; only the first-page-only order is dropped and the cursor replaced.
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.WithExpression(
            SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(
                SyntaxKind.CoalesceExpression,
                SyntaxFactory.IdentifierName("initialRequest"),
                SyntaxFactory.ImplicitObjectCreationExpression())),
            SyntaxFactory.InitializerExpression(
                SyntaxKind.WithInitializerExpression,
                SyntaxFactory.SeparatedList<ExpressionSyntax>(
                [
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("Order"),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("Cursor"),
                        SyntaxFactory.IdentifierName("cursor")),
                ])))));
        return SyntaxFactory.MethodDeclaration(TypeSyntaxEmitter.EmitNamed(requestTypeName), "CreateNextRequest")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("initialRequest"))
                    .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed(requestTypeName))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("cursor"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
            ])))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                "Creates a continuation request carrying every initial filter beside the opaque next cursor, "
                + "with the first-page-only order dropped."));
    }

    /// <summary>
    /// Every success path is one deserialization pass: bare bodies read the payload type,
    /// wrapped bodies read their internal envelope DTO and project its members, and a
    /// no-content success ignores any unexpected body.
    /// </summary>
    private static ExpressionSyntax EmitSuccessCreation(EnvelopePlan envelope, ExpressionSyntax body)
    {
        ExpressionSyntax Read()
        {
            var readTypeName = envelope.EnvelopeDtoTypeName
                               ?? SerializerTypeNamePolicy.ContextPropertyName(envelope.PayloadType!);
            return EmissionSyntax.Invocation(
                SyntaxFactory.IdentifierName("ReadBarePayload"),
                SyntaxFactory.Argument(body),
                SyntaxFactory.Argument(EmissionSyntax.MemberAccess(
                    RegistryNames.Default,
                    readTypeName)));
        }

        return envelope.Kind switch
        {
            // The materializer hands a binary success to AdaptBinary with its raw bytes; the text
            // paths never see one.
            EnvelopeKind.Binary => SyntaxFactory.ThrowExpression(SyntaxFactory.ObjectCreationExpression(
                    TypeSyntaxEmitter.EmitNamed("InvalidOperationException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal("A binary success is adapted from its raw bytes, not from text."))))))),
            EnvelopeKind.Bare => EmitSuccessInitializer(envelope, Read()),
            EnvelopeKind.Data => EmitSuccessInitializer(envelope, EmissionSyntax.MemberAccess(Read(), "Data")),
            EnvelopeKind.NoContent => EmitNoContentSuccess(envelope),
            EnvelopeKind.CursorList or EnvelopeKind.DataLocation or EnvelopeKind.DataLocationList or _ =>
                EmissionSyntax.Invocation(
                    SyntaxFactory.IdentifierName("CreateSuccess"),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("status")),
                    SyntaxFactory.Argument(Read())),
        };
    }

    /// <summary>A declared no-content status determines success regardless of an unexpected body.</summary>
    private static ObjectCreationExpressionSyntax EmitNoContentSuccess(EnvelopePlan envelope) =>
        SyntaxFactory.ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName))
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList<ExpressionSyntax>(
                [
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("Status"),
                        SyntaxFactory.IdentifierName("status")),
                ])));

    private static ObjectCreationExpressionSyntax EmitSuccessInitializer(EnvelopePlan envelope, ExpressionSyntax payload) =>
        SyntaxFactory.ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName))
            .WithInitializer(SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression,
                SyntaxFactory.SeparatedList<ExpressionSyntax>(
                [
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("Status"),
                        SyntaxFactory.IdentifierName("status")),
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(RequirePayloadName(envelope)),
                        payload),
                ])));

    private static string RequirePayloadName(EnvelopePlan envelope) =>
        envelope.PayloadName
        ?? throw new InvalidOperationException($"Envelope '{envelope.ResponseTypeName}' has no payload.");

    /// <summary>Sibling-carrying envelopes project the DTO twice, so a private helper keeps the switch arm single-read.</summary>
    private static MethodDeclarationSyntax EmitProjectingSuccessHelper(EnvelopePlan envelope, string siblingName)
    {
        var dtoTypeName = envelope.EnvelopeDtoTypeName
                          ?? throw new InvalidOperationException($"Envelope '{envelope.ResponseTypeName}' has no DTO.");
        return SyntaxFactory.MethodDeclaration(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName), "CreateSuccess")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("status"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("envelope"))
                    .WithType(TypeSyntaxEmitter.EmitNamed(dtoTypeName)),
            ])))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName))
                    .WithInitializer(SyntaxFactory.InitializerExpression(
                        SyntaxKind.ObjectInitializerExpression,
                        SyntaxFactory.SeparatedList<ExpressionSyntax>(
                        [
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName("Status"),
                                SyntaxFactory.IdentifierName("status")),
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(RequirePayloadName(envelope)),
                                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("envelope"), "Data")),
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(siblingName),
                                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("envelope"), siblingName)),
                        ])))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static ObjectCreationExpressionSyntax EmitErrorCreation(EnvelopePlan envelope, ExpressionSyntax allowedTags) =>
        SyntaxFactory.ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(envelope.ResponseTypeName))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("status")),
                SyntaxFactory.Argument(EmissionSyntax.Invocation(
                    SyntaxFactory.IdentifierName("ReadTolerantError"),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("rawBody")),
                    SyntaxFactory.Argument(allowedTags))),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("rawBody")),
            ])));

    private static LiteralExpressionSyntax Number(int value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));

    private static string TagSetName(int statusCode) =>
        $"Status{statusCode.ToString(CultureInfo.InvariantCulture)}Tags";
}
