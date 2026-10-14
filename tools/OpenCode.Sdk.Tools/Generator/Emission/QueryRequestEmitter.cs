using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits one public request record per query-carrying operation: derived records inherit the
/// <c>ListRequest</c> trio verbatim, flat records declare every bound query property.
/// </summary>
internal static class QueryRequestEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ClientPlan> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);

        return Array.AsReadOnly(
        [
            .. clients
                .SelectMany(static client => client.Operations)
                .Where(static operation => operation.QueryRequest is { RidesRequestBody: false })
                .OrderBy(static operation => operation.QueryRequest!.TypeName, StringComparer.Ordinal)
                .Select(static operation => EmitQueryRequest(operation)),
        ]);
    }

    private static GeneratedSource EmitQueryRequest(OperationPlan operation)
    {
        var queryRequest = operation.QueryRequest!;
        var declaration = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), queryRequest.TypeName)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(operation.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(
            [
                .. queryRequest.Properties
                    .Where(static property => !property.IsInherited)
                    .Select(static property => EmitProperty(property)),
            ]))
            .WithLeadingTrivia(EmissionSyntax.Documentation(RequestDocumentation(operation)));
        if (queryRequest.DerivesFromListRequest)
        {
            declaration = declaration.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.EmitNamed("ListRequest")))));
        }

        // Every other query kind is a spine type in the client namespace; only a generated
        // enum lives with the models.
        var usings = queryRequest.Properties.Any(static property => property.Kind is QueryValueKind.Enum)
            ? new[] { GeneratedNamespace.Models, }
            : [];
        var unit = EmissionSyntax.CompilationUnit(GeneratedNamespace.Public, usings, [declaration]);
        return EmissionSyntax.CreateSource(
            GeneratedPlacement.PathOf(GeneratedKind.OperationRecord, TypeHome.Family(operation.RouteContainerName), queryRequest.TypeName),
            unit);
    }

    internal static PropertyDeclarationSyntax EmitProperty(QueryPropertyPlan property)
    {
        var accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(
        [
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
            SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
        ]));
        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword), };
        if (property.IsRequired)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.RequiredKeyword));
        }

        return SyntaxFactory.PropertyDeclaration(EmitPropertyType(property), property.PropertyName)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithAccessorList(accessors)
            .WithLeadingTrivia(EmissionSyntax.Documentation(property.Description ?? (property.IsRequired
                ? $"Gets the required '{property.WireName}' query value."
                : $"Gets the '{property.WireName}' query value; the server default applies when unset.")));
    }

    /// <summary>A required parameter is carried non-nullable; an optional one stays nullable so an unset member is omitted.</summary>
    private static TypeSyntax EmitPropertyType(QueryPropertyPlan property)
    {
        var type = property.Kind switch
        {
            QueryValueKind.Text or QueryValueKind.RouteTail => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
            QueryValueKind.ListOrder => TypeSyntaxEmitter.EmitNamed("ListOrder"),
            QueryValueKind.BooleanText => TypeSyntaxEmitter.EmitNamed("QueryBoolean"),
            QueryValueKind.SessionParentFilter => TypeSyntaxEmitter.EmitNamed("SessionParentFilter"),
            QueryValueKind.Location => TypeSyntaxEmitter.EmitNamed("LocationSelector"),
            QueryValueKind.Enum => TypeSyntaxEmitter.EmitNamed(property.EnumTypeName
                ?? throw new InvalidOperationException($"Query property '{property.PropertyName}' has no enum type name.")),
            _ => throw new InvalidOperationException($"Query value kind '{property.Kind}' has no property type."),
        };
        return property.IsRequired ? type : SyntaxFactory.NullableType(type);
    }

    private static string RequestDocumentation(OperationPlan operation)
    {
        var summary = $"Shapes the '{operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}' query.";
        var inherited = operation.QueryRequest!.Properties
            .Where(static property => property.IsInherited && !string.IsNullOrWhiteSpace(property.Description))
            .Select(static property => $"'{property.WireName}': {property.Description}")
            .ToArray();
        return inherited.Length is 0 ? summary : $"{summary} {string.Join(' ', inherited)}";
    }
}
