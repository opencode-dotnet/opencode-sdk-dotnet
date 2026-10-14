using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>Emits one operation method delegating once into the pipeline.</summary>
internal static class OperationMethodEmitter
{
    public static MethodDeclarationSyntax Emit(OperationPlan operation, EmissionMode emission, bool classMarked = false)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var methodParameters = operation
            .Parameters
            .Where(static parameter => !parameter.IsHandleParameter)
            .ToArray();
        var statements = new List<StatementSyntax>();
        foreach (var parameter in methodParameters)
        {
            statements.AddRange(EmissionSyntax.ArgumentNullOrEmptyGuard(parameter.Name));
        }

        if (operation.RequestBody is { IsOptional: false, IsBinary: false } requiredBody)
        {
            statements.AddRange(EmissionSyntax.ArgumentNullGuard(requiredBody.ParameterName));
        }

        // A query request carrying a required member is itself required, and guards exactly
        // like a required body does.
        if (operation.QueryRequest is { RidesRequestBody: false, HasRequiredMember: true })
        {
            statements.AddRange(EmissionSyntax.ArgumentNullGuard(ReservedNamePolicy.RequestParameter));
        }

        // A raw-byte body is the last wire parameter, so it guards last.
        if (operation.RequestBody is { IsBinary: true } binaryBody)
        {
            statements.AddRange(EmissionSyntax.ArgumentNullGuard(binaryBody.ParameterName));
        }

        statements.AddRange(EmitDeclaredHeaderCollection(operation));
        statements.Add(SyntaxFactory.ReturnStatement(EmitDelegation(operation)));
        var returnType = operation.Stream is { } streaming
            ? TypeSyntaxEmitter.Generic("IAsyncEnumerable", TypeSyntaxEmitter.EmitNamed(streaming.PayloadTypeName))
            : TypeSyntaxEmitter.Generic("Task", TypeSyntaxEmitter.EmitNamed(operation.Envelope!.ResponseTypeName));
        return SyntaxFactory
            .MethodDeclaration(returnType, operation.MethodName)
            .WithModifiers(EmissionModifiers.Member(emission))
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(emission is EmissionMode.Public && !classMarked ? operation.ExperimentalDiagnostic : null))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(EmitParameters(operation, methodParameters))))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(EmitDocumentation(operation, methodParameters));
    }

    /// <summary>
    /// Collects the supplied declared headers into the value the pipeline sends. A header is
    /// optional on the wire, so an omitted one contributes nothing rather than an empty value.
    /// </summary>
    private static IEnumerable<StatementSyntax> EmitDeclaredHeaderCollection(OperationPlan operation)
    {
        if (operation.DeclaredHeaders.Count is 0)
        {
            yield break;
        }

        var local = SyntaxFactory.IdentifierName(ReservedNamePolicy.DeclaredHeadersParameter);
        yield return SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory
                .VariableDeclarator(ReservedNamePolicy.DeclaredHeadersParameter)
                .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory
                    .ObjectCreationExpression(TypeSyntaxEmitter.Generic("List", TypeSyntaxEmitter.EmitNamed("DeclaredHeader")))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(operation.DeclaredHeaders.Count)))))))))));
        foreach (var header in operation.DeclaredHeaders)
        {
            var value = SyntaxFactory.IdentifierName(header.Name);
            yield return SyntaxFactory.IfStatement(
                SyntaxFactory.IsPatternExpression(
                    value,
                    SyntaxFactory.UnaryPattern(SyntaxFactory.ConstantPattern(
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)))),
                SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(local, "Add"),
                    SyntaxFactory.Argument(SyntaxFactory
                        .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed("DeclaredHeader"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                        [
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(header.WireName))),
                            SyntaxFactory.Argument(value),
                        ]))))))));
        }
    }

    private static IEnumerable<ParameterSyntax> EmitParameters(OperationPlan operation,
        IReadOnlyList<OperationParameterPlan> methodParameters)
    {
        foreach (var parameter in methodParameters)
        {
            yield return SyntaxFactory
                .Parameter(SyntaxFactory.Identifier(parameter.Name))
                .WithType(TypeSyntaxEmitter.EmitNamed(parameter.TypeName));
        }

        if (operation.RequestBody is { IsBinary: false })
        {
            var body = SyntaxFactory.Parameter(SyntaxFactory.Identifier(operation.RequestBody.ParameterName));
            yield return operation.RequestBody.IsOptional
                ? body
                    .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed(operation.RequestBody.TypeName)))
                    .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)))
                : body.WithType(TypeSyntaxEmitter.EmitNamed(operation.RequestBody.TypeName));
        }

        // A merged request already surfaced as the body parameter above.
        if (operation.QueryRequest is { RidesRequestBody: false } queryRequest)
        {
            var request = SyntaxFactory.Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.RequestParameter));
            yield return queryRequest.HasRequiredMember
                ? request.WithType(TypeSyntaxEmitter.EmitNamed(queryRequest.TypeName))
                : request
                    .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed(queryRequest.TypeName)))
                    .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        }

        // A raw-byte body follows the request record it travels with: the record addresses the
        // file, the stream fills it.
        if (operation.RequestBody is { IsBinary: true } binaryBody)
        {
            yield return SyntaxFactory
                .Parameter(SyntaxFactory.Identifier(binaryBody.ParameterName))
                .WithType(TypeSyntaxEmitter.EmitNamed(binaryBody.TypeName));
        }

        // Declared headers close the wire inputs, ahead of the SDK's own per-call knobs.
        foreach (var header in operation.DeclaredHeaders)
        {
            yield return SyntaxFactory
                .Parameter(SyntaxFactory.Identifier(header.Name))
                .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed("string")))
                .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        }

        if (operation.Stream is null)
        {
            yield return SyntaxFactory
                .Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.RequestOptionsParameter))
                .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed("OpenCodeRequestOptions")))
                .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        }

        yield return SyntaxFactory
            .Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.CancellationTokenParameter))
            .WithType(TypeSyntaxEmitter.EmitNamed("CancellationToken"))
            .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));
    }

    private static InvocationExpressionSyntax EmitDelegation(OperationPlan operation)
    {
        // HttpMethod.Patch is absent from the downlevel BCL, so it rides the internal spine.
        var verbContainer = string.Equals(operation.HttpMethod, "patch", StringComparison.Ordinal)
            ? "OpenCodeHttpMethod"
            : "HttpMethod";
        var arguments = new List<ArgumentSyntax>
        {
            SyntaxFactory.Argument(EmissionSyntax.MemberAccess(
                SyntaxFactory.IdentifierName(verbContainer),
                CSharpNamePolicy.ToPascalCase(operation.HttpMethod))),
            SyntaxFactory.Argument(EmitRoute(operation)),
        };
        if (operation.RequestBody is not null)
        {
            arguments.Add(SyntaxFactory.Argument(EmitBodyArgument(operation.RequestBody)));
            if (!operation.RequestBody.IsBinary)
            {
                arguments.Add(SyntaxFactory.Argument(EmissionSyntax.MemberAccess(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("OpenCodeJsonContext"), "Default"),
                    operation.RequestBody.TypeName)));
            }
        }

        // A stream yields its payloads directly, so it carries no per-call options (ADR-0007).
        arguments.Add(SyntaxFactory.Argument(EmissionSyntax.MemberAccess(
            SyntaxFactory.IdentifierName(operation.Stream?.AdapterTypeName ?? operation.Envelope!.AdapterTypeName),
            "Instance")));
        if (operation.Stream is null)
        {
            arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestOptionsParameter)));
        }

        // The collected headers are named so the overload reads at the call site and so the
        // channel cannot bind silently to the wrong pipeline parameter.
        if (operation.DeclaredHeaders.Count > 0)
        {
            arguments.Add(SyntaxFactory
                .Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.DeclaredHeadersParameter))
                .WithNameColon(SyntaxFactory.NameColon(ReservedNamePolicy.DeclaredHeadersParameter)));
        }

        arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.CancellationTokenParameter)));
        return EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(
                SyntaxFactory.IdentifierName("Pipeline"),
                operation.Stream is null ? "ExecuteAsync" : "ExecuteStreamAsync"),
            [.. arguments]);
    }

    /// <summary>An omitted optional body still sends the empty JSON object the wire requires.</summary>
    private static ExpressionSyntax EmitBodyArgument(RequestBodyPlan body)
    {
        var parameter = SyntaxFactory.IdentifierName(body.ParameterName);
        if (!body.IsOptional)
        {
            return parameter;
        }

        // The client caches one empty instance per optional body type; the generated request
        // records are immutable, so sharing replaces a per-call allocation.
        return SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            parameter,
            SyntaxFactory.IdentifierName(EmptyBodyFieldName(body.TypeName)));
    }

    /// <summary>Names the client's cached empty instance for an optional request-body type.</summary>
    internal static string EmptyBodyFieldName(string typeName) => $"Empty{typeName}";

    /// <summary>Route arguments come from the method for ordinary parameters and from the bound handle state otherwise.</summary>
    private static ExpressionSyntax EmitRoute(OperationPlan operation)
    {
        var member = EmissionSyntax.MemberAccess(
            EmissionSyntax.MemberAccess(
                SyntaxFactory.IdentifierName("OpenCodeRoutes"),
                operation.RouteContainerName),
            operation.RouteMemberName);
        if (operation.Parameters.Count is 0 && operation.QueryRequest is null)
        {
            return member;
        }

        var arguments = new List<ArgumentSyntax>();
        arguments.AddRange(operation.Parameters.Select(static parameter => SyntaxFactory.Argument(parameter.IsHandleParameter
            ? SyntaxFactory.IdentifierName(CSharpNamePolicy.ToPascalCase(parameter.Name))
            : SyntaxFactory.IdentifierName(parameter.Name))));
        if (operation.QueryRequest is not null)
        {
            arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestParameter)));
        }

        return EmissionSyntax.Invocation(member, [.. arguments]);
    }

    private static SyntaxTriviaList EmitDocumentation(OperationPlan operation, IReadOnlyList<OperationParameterPlan> methodParameters)
    {
        var parameters = new List<DocumentedParameter>();
        parameters.AddRange(methodParameters.Select(static parameter =>
            new DocumentedParameter(parameter.Name, $"The '{parameter.WireName}' route value.")));
        if (operation.RequestBody is { IsBinary: false })
        {
            parameters.Add(new DocumentedParameter(
                operation.RequestBody.ParameterName,
                operation.RequestBody.IsOptional
                    ? "The request body; an empty body is sent when omitted."
                    : "The request body."));
        }

        if (operation.QueryRequest is { RidesRequestBody: false } documentedQuery)
        {
            parameters.Add(new DocumentedParameter(
                ReservedNamePolicy.RequestParameter,
                documentedQuery.HasRequiredMember
                    ? "The request shaping the query; its required members have no server default."
                    : "The request shaping the query."));
        }

        if (operation.RequestBody is { IsBinary: true } binaryBody)
        {
            parameters.Add(new DocumentedParameter(
                binaryBody.ParameterName,
                "The bytes to send, read from the stream's current position to its end; the stream is neither disposed nor rewound."));
        }

        parameters.AddRange(operation.DeclaredHeaders.Select(static header =>
            new DocumentedParameter(header.Name, $"The '{header.WireName}' request header; omitted when null.")));

        if (operation.Stream is null)
        {
            parameters.Add(new DocumentedParameter(ReservedNamePolicy.RequestOptionsParameter, "The per-call options."));
        }

        parameters.Add(new DocumentedParameter(ReservedNamePolicy.CancellationTokenParameter, "The cancellation token."));

        var exceptions = new List<DocumentedException>();
        if (operation.ErrorMap.Statuses.Count > 0)
        {
            var statuses = string.Join(
                ", ",
                operation.ErrorMap.Statuses.Select(static status => status.StatusCode.ToString(CultureInfo.InvariantCulture)));
            exceptions.Add(new DocumentedException(
                "OpenCodeApiException",
                operation.Stream is null
                    ? $"The API returned an error status (declared: {statuses}) and NoThrow was not selected."
                    : $"The API returned a declared error status (declared: {statuses}); streaming API errors always throw."));
        }
        else
        {
            exceptions.Add(new DocumentedException(
                "OpenCodeApiException",
                operation.Stream is null
                    ? "The API returned an error status and NoThrow was not selected."
                    : "The API returned an error status; streaming API errors always throw."));
        }

        if (operation.Stream is not null)
        {
            exceptions.Add(new DocumentedException(
                "OpenCodeStreamFailureException",
                "The opened stream reported a schema-valid failure with a typed cause."));
        }

        exceptions.Add(new DocumentedException(
            "OpenCodeTransportException",
            operation.Stream is null
                ? "The server could not be reached or returned a malformed success body."
                : "The server could not be reached, the stream could not be read, or a frame or failure cause was malformed."));
        return EmissionSyntax.MemberDocumentation(
            DocumentationSummary(operation),
            parameters,
            operation.Stream is { } streamed
                ? $"The '{streamed.PayloadTypeName}' stream."
                : $"The '{operation.Envelope!.ResponseTypeName}' envelope.",
            exceptions,
            OperationRemarks.Of(operation));
    }

    private static string DocumentationSummary(OperationPlan operation)
    {
        if (operation.Summary is null)
        {
            return operation.Description ?? $"Calls the '{operation.RouteTemplate}' operation.";
        }

        if (operation.Description is null)
        {
            return operation.Summary;
        }

        var separator = operation.Summary.Length > 0 && operation.Summary[^1] is '.' ? " " : ". ";
        return string.Concat(operation.Summary, separator, operation.Description);
    }
}
