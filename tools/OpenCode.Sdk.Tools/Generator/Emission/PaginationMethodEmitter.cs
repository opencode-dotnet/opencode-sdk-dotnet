using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>Emits an automatic item traversal delegating into the hand-written cursor paginator.</summary>
internal static class PaginationMethodEmitter
{
    public static MethodDeclarationSyntax Emit(OperationPlan operation, EmissionMode emission, bool classMarked = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var pagination = operation.Pagination
                         ?? throw new InvalidOperationException($"Operation '{operation.MethodName}' is not paginated.");
        var delegation = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("CursorPaginator"), "EnumerateAsync"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(operation.MethodName)),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestParameter)),
            SyntaxFactory.Argument(EmissionSyntax.MemberAccess(
                SyntaxFactory.IdentifierName(operation.Envelope!.AdapterTypeName),
                "Instance")),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestOptionsParameter)),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.CancellationTokenParameter)));
        return SyntaxFactory
            .MethodDeclaration(
                TypeSyntaxEmitter.Generic(
                    "CursorSequence",
                    TypeSyntaxEmitter.EmitNamed(pagination.PageTypeName),
                    TypeSyntaxEmitter.EmitNamed(pagination.ItemTypeName)),
                pagination.MethodName)
            .WithModifiers(EmissionModifiers.Member(emission))
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(emission is EmissionMode.Public && !classMarked ? operation.ExperimentalDiagnostic : null))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.RequestParameter))
                    .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed(pagination.RequestTypeName)))
                    .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.RequestOptionsParameter))
                    .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed("OpenCodeRequestOptions")))
                    .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.CancellationTokenParameter))
                    .WithType(TypeSyntaxEmitter.EmitNamed("CancellationToken"))
                    .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))),
            ])))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(delegation)))
            .WithLeadingTrivia(EmitDocumentation(operation, pagination));
    }

    private static SyntaxTriviaList EmitDocumentation(OperationPlan operation, PaginationPlan pagination)
    {
        var exceptions = new List<DocumentedException>();
        if (operation.ErrorMap.Statuses.Count > 0)
        {
            var statuses = string.Join(
                ", ",
                operation.ErrorMap.Statuses.Select(static status => status.StatusCode.ToString(CultureInfo.InvariantCulture)));
            exceptions.Add(new DocumentedException(
                "OpenCodeApiException",
                $"The API returned a declared error status (declared: {statuses}); pagination API errors always throw."));
        }
        else
        {
            exceptions.Add(new DocumentedException(
                "OpenCodeApiException",
                "The API returned an error status; pagination API errors always throw."));
        }

        exceptions.Add(new DocumentedException(
            "OpenCodeTransportException",
            "The server could not be reached or returned a malformed success body."));
        return DocumentationSyntax.MemberDocumentation(
            $"Enumerates the items returned by '{operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}' "
            + "by following each opaque next cursor; every filter of the first request rides each continuation.",
            [
                new DocumentedParameter(ReservedNamePolicy.RequestParameter, "The request shaping the first page."),
                new DocumentedParameter(ReservedNamePolicy.RequestOptionsParameter, "The per-call options applied to every page; NoThrow is refused."),
                new DocumentedParameter(ReservedNamePolicy.CancellationTokenParameter, "The cancellation token."),
            ],
            $"The '{pagination.ItemTypeName}' sequence, whose Pages expose each '{pagination.PageTypeName}'.",
            exceptions,
            [
                .. OperationRemarks.Of(operation),
                DocumentationRun.Prose(" See "),
                DocumentationRun.Reference($"{operation.MethodName}({pagination.RequestTypeName}, OpenCodeRequestOptions, CancellationToken)"),
                DocumentationRun.Prose(". "),
                .. operation.CuratedSummary is not null
                    ? DocumentationSyntax.NormalizedCuratedRuns(DocumentationSyntax.DocumentationSummary(operation))
                    : DocumentationSyntax.NormalizedProseRuns(DocumentationSyntax.DocumentationSummary(operation)),
            ]);
    }
}
