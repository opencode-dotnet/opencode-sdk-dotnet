using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class RoutesEmitter
{
    /// <summary>
    /// Emits the internal route surface as one part of a partial class per family, each in its
    /// family's Internal folder. An enum query converter is emitted once, in the part of the first family
    /// (ordinal) whose operations use it, so an enum two families share is still converted in
    /// exactly one place.
    /// </summary>
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ClientPlan> clients, IReadOnlyList<ModelPlan> models)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(models);

        var containers = clients
            .SelectMany(static client => client.Operations)
            .GroupBy(static operation => operation.RouteContainerName, StringComparer.Ordinal)
            .OrderBy(static container => container.Key, StringComparer.Ordinal)
            .ToArray();
        var convertedEnums = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<GeneratedSource>(containers.Length);
        foreach (var container in containers)
        {
            var ownedEnums = QueryEnumWireValueEmitter.EnumTypeNames([.. container]).Where(convertedEnums.Add).ToArray();
            result.Add(EmitPart(container, ownedEnums, models, documented: result.Count is 0));
        }

        return Array.AsReadOnly([.. result]);
    }

    private static GeneratedSource EmitPart(IGrouping<string, OperationPlan> container, string[] ownedEnums,
        IReadOnlyList<ModelPlan> models, bool documented)
    {
        var operations = container.ToArray();
        var hasQueryEnums = ownedEnums.Length > 0 || QueryEnumWireValueEmitter.EnumTypeNames(operations).Any();
        var members = new List<MemberDeclarationSyntax> { EmitContainer(container) };
        members.AddRange(QueryEnumWireValueEmitter.Emit(ownedEnums, models));
        var declaration = SyntaxFactory.ClassDeclaration("OpenCodeRoutes")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
            .WithMembers(SyntaxFactory.List(members));

        // A partial type takes its documentation from one part; the others would concatenate onto it.
        if (documented)
        {
            declaration = declaration.WithLeadingTrivia(EmissionSyntax.Documentation("Defines the wire route of every generated operation."));
        }

        var usings = new List<string>();

        // System carries the route-value guards, the required-request guard, and the
        // out-of-range refusal an enum converter falls through to.
        if (hasQueryEnums
            || operations.Any(static operation => operation.Parameters.Count > 0
                                                  || operation.QueryRequest is { HasRequiredMember: true }))
        {
            usings.Add("System");
        }

        // A merged request types its route builder with the body model, and an enum query
        // member types its converter with the generated enum.
        if (hasQueryEnums || operations.Any(static operation => operation.QueryRequest is { RidesRequestBody: true }))
        {
            usings.Add(GeneratedNamespace.Models);
        }

        var unit = EmissionSyntax.CompilationUnit(GeneratedNamespace.Internal, usings, [declaration]);
        return EmissionSyntax.CreateSource(
            GeneratedPlacement.PathOf(GeneratedKind.Routes, TypeHome.Family(container.Key), "OpenCodeRoutes"),
            unit);
    }

    private static ClassDeclarationSyntax EmitContainer(IGrouping<string, OperationPlan> container)
    {
        var members = new List<MemberDeclarationSyntax>();
        foreach (var operation in container.OrderBy(static operation => operation.RouteMemberName, StringComparer.Ordinal))
        {
            var diagnostic = operation.RouteContainerExperimentalDiagnostic is null ? operation.ExperimentalDiagnostic : null;
            if (operation.Parameters.Count is 0 && operation.QueryRequest is null)
            {
                members.Add(EmitConst(
                    operation.RouteMemberName,
                    operation.RouteTemplate,
                    $"The '{operation.HttpMethod.ToUpperInvariant()} {operation.RouteTemplate}' route.", diagnostic));
                continue;
            }

            members.Add(EmitBuilder(operation, diagnostic));
        }

        return SyntaxFactory.ClassDeclaration(container.Key)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(container.First().RouteContainerExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Defines the '{container.Key}' routes."));
    }

    private static FieldDeclarationSyntax EmitConst(string name, string value, string documentation, string? diagnostic) =>
        SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(name)
                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(value)))))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.ConstKeyword)))
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(diagnostic))
            .WithLeadingTrivia(EmissionSyntax.Documentation(documentation));

    private static MethodDeclarationSyntax EmitBuilder(OperationPlan operation, string? diagnostic)
    {
        var statements = new List<StatementSyntax>();
        foreach (var parameter in operation.Parameters)
        {
            statements.AddRange(EmissionSyntax.RouteValueGuard(parameter.Name));
        }

        if (operation.QueryRequest is { HasRequiredMember: true })
        {
            statements.AddRange(EmissionSyntax.ArgumentNullGuard(ReservedNamePolicy.RequestParameter));
        }

        if (operation.QueryRequest is null)
        {
            statements.Add(SyntaxFactory.ReturnStatement(EmitConcatenation(operation)));
        }
        else
        {
            statements.AddRange(EmitQueryComposition(operation));
        }

        var parameters = new List<DocumentedParameter>();
        parameters.AddRange(operation.Parameters.Select(static parameter =>
            new DocumentedParameter(parameter.Name, $"The '{parameter.WireName}' route value.")));
        if (operation.QueryRequest is not null)
        {
            parameters.Add(new DocumentedParameter(ReservedNamePolicy.RequestParameter, "The request shaping the query."));
        }

        var documentation = EmissionSyntax.MemberDocumentation(
            $"Builds the '{operation.RouteTemplate}' route.",
            parameters,
            "The escaped route.");
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                operation.RouteMemberName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(EmitBuilderParameters(operation))))
            .WithBody(SyntaxFactory.Block(statements))
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(diagnostic))
            .WithLeadingTrivia(documentation);
    }

    private static IEnumerable<ParameterSyntax> EmitBuilderParameters(OperationPlan operation)
    {
        foreach (var parameter in operation.Parameters)
        {
            yield return SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name))
                .WithType(TypeSyntaxEmitter.EmitNamed(parameter.TypeName));
        }

        if (operation.QueryRequest is { } queryRequest)
        {
            var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.RequestParameter));
            yield return queryRequest.HasRequiredMember
                ? parameter.WithType(TypeSyntaxEmitter.EmitNamed(queryRequest.TypeName))
                : parameter
                    .WithType(SyntaxFactory.NullableType(TypeSyntaxEmitter.EmitNamed(queryRequest.TypeName)))
                    .WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        }
    }

    /// <summary>
    /// An unset optional request short-circuits to the bare path; a set one appends the
    /// composed query suffix. A request carrying a required member is never unset, so it
    /// composes unconditionally.
    /// </summary>
    private static IEnumerable<StatementSyntax> EmitQueryComposition(OperationPlan operation)
    {
        yield return SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("path")
                .WithInitializer(SyntaxFactory.EqualsValueClause(EmitConcatenation(operation))))));
        if (!operation.QueryRequest!.HasRequiredMember)
        {
            yield return SyntaxFactory.IfStatement(
                SyntaxFactory.IsPatternExpression(
                    SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestParameter),
                    SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                SyntaxFactory.Block(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("path"))));
        }

        yield return SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("query")
                .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(
                        TypeSyntaxEmitter.EmitNamed("QueryStringBuilder"))
                    .WithArgumentList(SyntaxFactory.ArgumentList()))))));
        foreach (var property in operation.QueryRequest.Properties.Where(static property => property.Kind is not QueryValueKind.RouteTail))
        {
            var arguments = new List<ArgumentSyntax>
            {
                SyntaxFactory.Argument(StringLiteral(property.WireName)),
                SyntaxFactory.Argument(EmitValueArgument(property)),
            };
            yield return SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("query"), QueryAddMethod(property.Kind)),
                [.. arguments]));
        }

        yield return SyntaxFactory.ReturnStatement(SyntaxFactory.BinaryExpression(
            SyntaxKind.AddExpression,
            SyntaxFactory.IdentifierName("path"),
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("query"), "Value")));
    }

    /// <summary>
    /// An enum member reaches the builder already spelled for the wire, so the hand-written
    /// spine keeps one text channel instead of a per-enum overload it cannot know about.
    /// </summary>
    private static ExpressionSyntax EmitValueArgument(QueryPropertyPlan property)
    {
        var member = EmissionSyntax.MemberAccess(
            SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestParameter),
            property.PropertyName);
        return property.Kind is QueryValueKind.Enum
            ? EmissionSyntax.Invocation(
                SyntaxFactory.IdentifierName(QueryEnumWireValueEmitter.ConverterName),
                SyntaxFactory.Argument(member))
            : member;
    }

    private static string QueryAddMethod(QueryValueKind kind) => kind switch
    {
        QueryValueKind.Text or QueryValueKind.Enum => "AddText",
        QueryValueKind.ListOrder => "AddOrder",
        QueryValueKind.BooleanText => "AddBoolean",
        QueryValueKind.SessionParentFilter => "AddParentFilter",
        QueryValueKind.Location => "AddLocation",
        QueryValueKind.Int32 or QueryValueKind.Int64 => "AddInteger",
        QueryValueKind.RouteTail => throw new InvalidOperationException("The route tail fills the path, never the query."),
        _ => throw new InvalidOperationException($"Query value kind '{kind}' has no query-builder method."),
    };

    /// <summary>
    /// Guards one route value: null/empty/whitespace is refused, and so are the dot segments
    /// <c>Uri</c> canonicalization would silently rewrite into a different request path.
    /// </summary>
    /// <summary>Folds the template into literal + escaped-parameter concatenation, in template order.</summary>
    private static ExpressionSyntax EmitConcatenation(OperationPlan operation)
    {
        var pieces = new List<ExpressionSyntax>();
        var template = operation.RouteTemplate;
        var position = 0;
        foreach (var parameter in operation.Parameters)
        {
            var token = $"{{{parameter.WireName}}}";
            var start = template.IndexOf(token, position, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new InvalidOperationException(
                    $"Route template '{template}' does not contain parameter '{parameter.WireName}'.");
            }

            if (start > position)
            {
                pieces.Add(StringLiteral(template[position..start]));
            }

            pieces.Add(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("RouteValuePolicy"), "Escape"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Name)),
                SyntaxFactory.Argument(EmissionSyntax.Invocation(
                    SyntaxFactory.IdentifierName("nameof"),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Name))))));
            position = start + token.Length;
        }

        var tail = operation.QueryRequest?.Properties.SingleOrDefault(static property => property.Kind is QueryValueKind.RouteTail);
        var end = tail is null ? template.Length : template.Length - 1;
        if (position < end)
        {
            pieces.Add(StringLiteral(template[position..end]));
        }

        if (tail is not null)
        {
            // The trailing '*' takes the request's route-tail member, escaped segment by segment.
            pieces.Add(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("RouteValuePolicy"), "EscapeTail"),
                SyntaxFactory.Argument(EmissionSyntax.MemberAccess(
                    SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestParameter), tail.PropertyName)),
                SyntaxFactory.Argument(EmissionSyntax.Invocation(
                    SyntaxFactory.IdentifierName("nameof"),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ReservedNamePolicy.RequestParameter))))));
        }

        return pieces.Aggregate(static (left, right) =>
            SyntaxFactory.BinaryExpression(SyntaxKind.AddExpression, left, right));
    }

    private static LiteralExpressionSyntax StringLiteral(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));
}
