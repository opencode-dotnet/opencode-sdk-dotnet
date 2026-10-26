using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class EnvelopeEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ClientPlan> clients, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(namespaces);

        return Array.AsReadOnly(
        [
            .. EnvelopeOperations.Naming(clients, static envelope => envelope.ResponseTypeName)
                .Select(operation => EmitEnvelope(operation, namespaces)),
        ]);
    }

    private static GeneratedSource EmitEnvelope(OperationPlan operation, GeneratedNamespacePlan namespaces)
    {
        var envelope = operation.Envelope!;
        var members = envelope.Kind is EnvelopeKind.NoContent
            ? EmitNoContentMembers(envelope)
            : EmitPayloadMembers(envelope);
        var declaration = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), envelope.ResponseTypeName)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(operation.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.EmitNamed("OpenCodeResponse")))))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                $"Represents the response of the '{operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}' operation."));
        IReadOnlyList<string> usings = envelope.Kind switch
        {
            EnvelopeKind.NoContent => ["System.Diagnostics.CodeAnalysis", namespaces.Models],
            EnvelopeKind.CursorList or EnvelopeKind.DataLocationList =>
                ["System", "System.Diagnostics.CodeAnalysis", "System.Text", namespaces.Models],
            EnvelopeKind.Bare or EnvelopeKind.Data or EnvelopeKind.DataLocation or EnvelopeKind.Binary or _ =>
                ["System", "System.Diagnostics.CodeAnalysis", "System.Text", namespaces.Models],
        };
        declaration = DocumentationSyntax.DocumentedType(declaration, envelope.Documentation);
        var unit = EmissionSyntax.CompilationUnit(namespaces.Public, usings, [declaration]);
        return EmissionSyntax.CreateSource(
            GeneratedPlacement.PathOf(GeneratedKind.OperationRecord, TypeHome.Family(operation.RouteContainerName), envelope.ResponseTypeName),
            unit);
    }

    private static List<MemberDeclarationSyntax> EmitNoContentMembers(EnvelopePlan envelope) =>
    [
        EmitSuccessConstructor(envelope.ResponseTypeName),
        EmitErrorConstructor(envelope),
    ];

    private static List<MemberDeclarationSyntax> EmitPayloadMembers(EnvelopePlan envelope)
    {
        var payloadName = RequirePayloadName(envelope);
        var fieldName = $"_{CSharpNamePolicy.ToCamelCase(payloadName)}";
        var payloadType = PayloadType(envelope);

        // The field is always exactly one level nullable (it doubles as the error-path
        // sentinel). For a nullable payload, PayloadType(envelope) already carries the plan's
        // own '?' (TypeSyntaxEmitter.Emit), so EmitBackingField's wrap needs the non-nullable
        // syntax underneath it to avoid stacking a second '?'; for a non-nullable payload this
        // is the same syntax as payloadType, so the emission is unaffected.
        var payloadFieldType = TypeSyntaxEmitter.Emit(envelope.PayloadType! with { IsNullable = false });
        var members = new List<MemberDeclarationSyntax>
        {
            EmitBackingField(fieldName, payloadFieldType),
        };
        if (envelope.Kind is EnvelopeKind.CursorList)
        {
            members.Add(EmitBackingField("_cursor", TypeSyntaxEmitter.EmitNamed("ListCursor")));
        }

        if (envelope.LocationTypeName is not null)
        {
            members.Add(EmitBackingField("_location", TypeSyntaxEmitter.EmitNamed(envelope.LocationTypeName)));
        }

        members.Add(EmitSuccessConstructor(envelope.ResponseTypeName));
        members.Add(EmitErrorConstructor(envelope));
        members.Add(EmitPayloadProperty(envelope, payloadType, fieldName));
        if (envelope.Kind is EnvelopeKind.CursorList)
        {
            members.Add(EmitCursorProperty());
        }

        if (envelope.LocationTypeName is not null)
        {
            members.Add(EmitLocationProperty(envelope.LocationTypeName));
        }

        if (envelope.Kind is EnvelopeKind.Binary)
        {
            members.Add(EmitContentTypeProperty());
        }

        members.Add(EmitPrintMembers(envelope, payloadName, fieldName));
        return members;
    }

    private static string RequirePayloadName(EnvelopePlan envelope) =>
        envelope.PayloadName
        ?? throw new InvalidOperationException($"Envelope '{envelope.ResponseTypeName}' has no payload.");

    private static TypeSyntax PayloadType(EnvelopePlan envelope) => TypeSyntaxEmitter.Emit(envelope.PayloadType!);

    private static FieldDeclarationSyntax EmitBackingField(string fieldName, TypeSyntax payloadType) =>
        SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.NullableType(payloadType),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(fieldName))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static ConstructorDeclarationSyntax EmitSuccessConstructor(string responseTypeName) =>
        SyntaxFactory.ConstructorDeclaration(responseTypeName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithBody(SyntaxFactory.Block())
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                $"Initializes a success instance of the '{responseTypeName}' envelope."));

    private static ConstructorDeclarationSyntax EmitErrorConstructor(EnvelopePlan envelope)
    {
        var responseTypeName = envelope.ResponseTypeName;

        var assignments = new List<StatementSyntax>
        {
            Assign("Status", SyntaxFactory.IdentifierName("status")),
            Assign("IsError", SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)),
            Assign("Error", SyntaxFactory.IdentifierName("error")),
            Assign("RawBody", SyntaxFactory.IdentifierName("rawBody")),
        };

        // The payload assignment is the SDK's single null-forgiveness: the guarded getter
        // makes the null unobservable. A no-content envelope has no payload to forgive. A
        // nullable payload's property is already typed to accept null, so no suppression is
        // needed there — the response-state guard, not the field, is what makes it unobservable.
        // A binary payload is a value type: its nullable backing field is already null, and a
        // null cannot be assigned to the property.
        if (envelope.PayloadName is not null && envelope.Kind is not EnvelopeKind.Binary)
        {
            ExpressionSyntax nullValue = envelope.PayloadType!.IsNullable
                ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                : SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
            assignments.Add(Assign(envelope.PayloadName, nullValue));
        }

        if (envelope.Kind is EnvelopeKind.CursorList)
        {
            assignments.Add(Assign("Cursor", SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
        }

        if (envelope.LocationTypeName is not null)
        {
            assignments.Add(Assign("Location", SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))));
        }
        return SyntaxFactory.ConstructorDeclaration(responseTypeName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword)))
            .AddAttributeLists(EmissionSyntax.Attribute("SetsRequiredMembers"))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("status"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("error"))
                    .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed(ErrorMarkerPolicy.InterfaceTypeName))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("rawBody"))
                    .WithType(SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))),
            ])))
            .WithBody(SyntaxFactory.Block(assignments))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                "Initializes an error-path instance; the payload stays unset behind its guard."));
    }

    private static PropertyDeclarationSyntax EmitPayloadProperty(EnvelopePlan envelope, TypeSyntax payloadType, string fieldName)
    {
        var payloadName = RequirePayloadName(envelope);
        var documentation = $"Gets the {payloadName} payload; guarded on the error path.";
        return envelope.PayloadType!.IsNullable
            ? EmitResponseStateGuardedProperty(payloadType, payloadName, fieldName, documentation)
            : EmitGuardedProperty(
                payloadType,
                payloadName,
                fieldName,
                SyntaxFactory.IdentifierName("value"),
                documentation);
    }

    private static PropertyDeclarationSyntax EmitCursorProperty() =>
        EmitGuardedProperty(
            TypeSyntaxEmitter.EmitNamed("ListCursor"),
            "Cursor",
            "_cursor",
            SyntaxFactory.IdentifierName("value"),
            "Gets the page cursor; guarded on the error path.");

    private static PropertyDeclarationSyntax EmitLocationProperty(string locationTypeName) =>
        EmitGuardedProperty(
            TypeSyntaxEmitter.EmitNamed(locationTypeName),
            "Location",
            "_location",
            SyntaxFactory.IdentifierName("value"),
            "Gets the location the server resolved for the request; guarded on the error path.");

    private static PropertyDeclarationSyntax EmitContentTypeProperty() =>
        SyntaxFactory.PropertyDeclaration(
                SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword))),
                "ContentType")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(
            [
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            ])))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                "Gets the Content-Type the server declared for the bytes, or null when it declared none or the response is an error."));

    private static PropertyDeclarationSyntax EmitGuardedProperty(TypeSyntax propertyType, string propertyName,
        string fieldName, ExpressionSyntax initValue, string documentation)
    {
        var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.BinaryExpression(
                SyntaxKind.CoalesceExpression,
                SyntaxFactory.IdentifierName(fieldName),
                ThrowGuardExpression(propertyName))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        return BuildGuardedProperty(propertyType, propertyName, fieldName, initValue, documentation, getter);
    }

    /// <summary>
    /// A nullable payload's wire null is a legitimate success value that the backing field
    /// cannot distinguish from the error path's forced null (both leave the field null), so
    /// this getter checks <c>IsError</c> directly instead of coalescing on the field.
    /// <see cref="EmitGuardedProperty"/> stays untouched for every non-nullable member —
    /// payload, cursor, and location alike — so their emission is unaffected.
    /// </summary>
    private static PropertyDeclarationSyntax EmitResponseStateGuardedProperty(TypeSyntax propertyType,
        string propertyName, string fieldName, string documentation)
    {
        var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.ConditionalExpression(
                SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.IdentifierName("IsError")),
                SyntaxFactory.IdentifierName(fieldName),
                ThrowGuardExpression(propertyName))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        return BuildGuardedProperty(
            propertyType, propertyName, fieldName, SyntaxFactory.IdentifierName("value"), documentation, getter);
    }

    private static ThrowExpressionSyntax ThrowGuardExpression(string propertyName)
    {
        var guardMessage = $"The response is an error; check IsError before accessing {propertyName}.";
        return SyntaxFactory.ThrowExpression(SyntaxFactory.ObjectCreationExpression(
                TypeSyntaxEmitter.EmitNamed("InvalidOperationException"))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(guardMessage)))))));
    }

    private static PropertyDeclarationSyntax BuildGuardedProperty(TypeSyntax propertyType, string propertyName,
        string fieldName, ExpressionSyntax initValue, string documentation, AccessorDeclarationSyntax getter)
    {
        var setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(fieldName),
                initValue)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        return SyntaxFactory.PropertyDeclaration(propertyType, propertyName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.RequiredKeyword)))
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List([getter, setter])))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(documentation));
    }

    private static MethodDeclarationSyntax EmitPrintMembers(EnvelopePlan envelope, string payloadName, string fieldName)
    {
        var payloadField = SyntaxFactory.IdentifierName(fieldName);
        var builder = SyntaxFactory.IdentifierName("builder");
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.ArgumentNullGuard("builder"));
        statements.Add(SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("printed")
                .WithInitializer(SyntaxFactory.EqualsValueClause(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.BaseExpression(), "PrintMembers"),
                    SyntaxFactory.Argument(builder))))))));

        // A non-nullable payload's field is null only on the error path, so "field is null"
        // is the right skip test. A nullable payload's field is also null on its own success
        // path (a legitimate wire null), so the skip has to key off IsError instead — the
        // field-null success case must still print "Widget = " with an empty rendering,
        // matching the record's default null rendering.
        ExpressionSyntax skipCondition = envelope.PayloadType!.IsNullable
            ? SyntaxFactory.IdentifierName("IsError")
            : SyntaxFactory.IsPatternExpression(
                payloadField,
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        statements.Add(SyntaxFactory.IfStatement(
            skipCondition,
            SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("printed")))));
        statements.Add(SyntaxFactory.IfStatement(
            SyntaxFactory.IdentifierName("printed"),
            SyntaxFactory.Block(Discard(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(builder, "Append"),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(", "))))))));
        statements.Add(AppendMember($"{payloadName} = ", payloadField));
        if (envelope.Kind is EnvelopeKind.Binary)
        {
            statements.Add(AppendMember(", ContentType = ", SyntaxFactory.IdentifierName("ContentType")));
        }

        var diagnosticMembers = string.Empty;
        if (envelope.Kind is EnvelopeKind.CursorList)
        {
            statements.Add(AppendMember(", Cursor = ", SyntaxFactory.IdentifierName("_cursor")));
            diagnosticMembers += " and cursor";
        }

        if (envelope.LocationTypeName is not null)
        {
            statements.Add(AppendMember(", Location = ", SyntaxFactory.IdentifierName("_location")));
            diagnosticMembers += " and location";
        }

        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)));
        var presence = diagnosticMembers.Length == 0 ? "it is present" : "they are present";
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                "PrintMembers")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("builder"))
                    .WithType(TypeSyntaxEmitter.EmitNamed("StringBuilder")))))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                $"Prints the shared metadata and appends the payload{diagnosticMembers} only when {presence}."));
    }

    private static ExpressionStatementSyntax AppendMember(string label, ExpressionSyntax value) =>
        Discard(EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(
                EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("builder"), "Append"),
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(label)))),
                "Append"),
            SyntaxFactory.Argument(value)));

    private static ExpressionStatementSyntax Assign(string target, ExpressionSyntax value) =>
        SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(target),
            value));

    private static ExpressionStatementSyntax Discard(ExpressionSyntax expression) =>
        SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName("_"),
            expression));
}
