using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class ClientEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ClientPlan> clients, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(namespaces);

        return Array.AsReadOnly([.. clients.Select(client => EmitClient(client, namespaces))]);
    }

    private static GeneratedSource EmitClient(ClientPlan client, GeneratedNamespacePlan namespaces)
    {
        var members = new List<MemberDeclarationSyntax>();
        members.AddRange(EmitFields(client));
        members.AddRange(client.Role switch
        {
            ClientRole.Root => EmitRootConstructors(client),
            ClientRole.Collection => EmitPipelineConstructor(client, EmitPipelineAssignments(client)),
            ClientRole.Handle => EmitHandleConstructor(client),
            _ => throw new InvalidOperationException($"Unknown client role '{client.Role}'."),
        });

        // A sealed internal client has no derivable seam, so it takes no mocking constructor.
        if (client.Emission is not EmissionMode.InternalRaw)
        {
            members.Add(EmitMockConstructor(client));
        }

        if (client.Role is ClientRole.Root)
        {
            members.AddRange(EmitDispose());
        }

        members.AddRange(client.SubClients.Select(reference => EmitSubClientProperty(client, reference)));
        if (client.HandleFactory is not null)
        {
            members.Add(EmitHandleFactory(client, client.HandleFactory));
        }

        members.Add(EmitGuardedAccessor(client, "Pipeline", "Pipeline", "_pipeline"));
        if (client.HandleParameter is not null)
        {
            members.Add(EmitGuardedAccessor(
                client,
                CSharpNamePolicy.ToPascalCase(client.HandleParameter.Name),
                client.HandleParameter.TypeName,
                HandleFieldName(client.HandleParameter)));
        }

        members.AddRange(EmitOperationMembers(client));

        var declaration = SyntaxFactory.ClassDeclaration(client.Name)
            .WithModifiers(EmissionModifiers.Type(client.Emission))
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(client.Emission is EmissionMode.Public ? client.ExperimentalDiagnostic : null))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(DocumentationSyntax.Documentation(Summary(client)));
        if (client.Role is ClientRole.Root)
        {
            declaration = declaration.WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.EmitNamed("IDisposable")))));
        }

        declaration = DocumentationSyntax.DocumentedType(declaration, client.Documentation);
        var unit = EmissionSyntax.CompilationUnit(client.Namespace, EmitUsings(client, namespaces), [declaration]);
        return EmissionSyntax.CreateSource(PathOf(client), unit);
    }

    private static string PathOf(ClientPlan client)
    {
        if (client.ContainerName is null)
        {
            return GeneratedPlacement.PathOf(GeneratedKind.RootClient, null, client.Name);
        }

        var kind = client.Emission is EmissionMode.InternalRaw ? GeneratedKind.RawClient : GeneratedKind.Client;
        return GeneratedPlacement.PathOf(kind, TypeHome.Family(client.ContainerName), client.Name);
    }

    /// <summary>Collection and I/O namespaces exist only where an operation kind needs one.</summary>
    private static List<string> EmitUsings(ClientPlan client, GeneratedNamespacePlan namespaces)
    {
        var usings = new List<string>
        {
            "System",
            "System.Net.Http",
            "System.Threading",
            "System.Threading.Tasks",
            namespaces.Internal,
            namespaces.Models,
        };
        if (client.Operations.Any(static operation =>
                operation.Stream is not null || operation.Pagination is not null || operation.DeclaredHeaders.Count > 0))
        {
            usings.Add("System.Collections.Generic");
        }

        if (client.Operations.Any(static operation => operation.RequestBody is { IsBinary: true }))
        {
            usings.Add("System.IO");
        }

        return usings;
    }

    private static string Summary(ClientPlan client) => client.Role switch
    {
        ClientRole.Root => "The opencode API client and composition root.",
        ClientRole.Collection => $"The '{client.Name}' collection client.",
        ClientRole.Handle => $"A bound '{client.Name}' handle; it holds an immutable identifier and the shared pipeline.",
        _ => throw new InvalidOperationException($"Unknown client role '{client.Role}'."),
    };

    private static IEnumerable<MemberDeclarationSyntax> EmitOperationMembers(ClientPlan client)
    {
        foreach (var operation in client.Operations)
        {
            yield return OperationMethodEmitter.Emit(operation, client.Emission, client.ExperimentalDiagnostic is not null);
            if (operation.Pagination is not null)
            {
                yield return PaginationMethodEmitter.Emit(operation, client.Emission, client.ExperimentalDiagnostic is not null);
            }
        }
    }

    private static IEnumerable<MemberDeclarationSyntax> EmitFields(ClientPlan client)
    {
        foreach (var typeName in OptionalBodyTypeNames(client))
        {
            yield return EmitEmptyBodyField(typeName);
        }

        yield return EmitField("_pipeline", TypeSyntaxEmitter.EmitNamed("Pipeline"));
        if (client.HandleParameter is not null)
        {
            yield return EmitField(HandleFieldName(client.HandleParameter), TypeSyntaxEmitter.EmitNamed(client.HandleParameter.TypeName));
        }

        foreach (var reference in client.SubClients)
        {
            yield return EmitField(SubClientFieldName(reference), TypeSyntaxEmitter.EmitNamed(reference.TypeName));
        }
    }

    /// <summary>Distinct optional request-body types, in operation order.</summary>
    private static IEnumerable<string> OptionalBodyTypeNames(ClientPlan client)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in client.Operations)
        {
            if (operation.RequestBody is { IsOptional: true } body && seen.Add(body.TypeName))
            {
                yield return body.TypeName;
            }
        }
    }

    private static FieldDeclarationSyntax EmitEmptyBodyField(string typeName) =>
        SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                TypeSyntaxEmitter.EmitNamed(typeName),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory
                    .VariableDeclarator(OperationMethodEmitter.EmptyBodyFieldName(typeName))
                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ImplicitObjectCreationExpression())))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static FieldDeclarationSyntax EmitField(string name, TypeSyntax type) =>
        SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.NullableType(type),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(name))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static IEnumerable<MemberDeclarationSyntax> EmitRootConstructors(ClientPlan client)
    {
        yield return SyntaxFactory.ConstructorDeclaration(client.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("options"))
                    .WithType(TypeSyntaxEmitter.EmitNamed("OpenCodeClientOptions")))))
            .WithBody(SyntaxFactory.Block(EmitRootAssignments(client, httpClientArgument: null)))
            .WithLeadingTrivia(DocumentationSyntax.MemberDocumentation(
                "Initializes a client that owns its connection to the endpoint.",
                [new DocumentedParameter("options", "The client options; the endpoint is required.")]));
        yield return SyntaxFactory.ConstructorDeclaration(client.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("httpClient")).WithType(TypeSyntaxEmitter.EmitNamed("HttpClient")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(TypeSyntaxEmitter.EmitNamed("OpenCodeClientOptions")),
            ])))
            .WithBody(SyntaxFactory.Block(EmitRootAssignments(client, httpClientArgument: "httpClient")))
            .WithLeadingTrivia(DocumentationSyntax.MemberDocumentation(
                "Initializes a client over an injected transport the client never disposes; " +
                "friend-assembly test surface.",
                [
                    new DocumentedParameter("httpClient", "The injected HTTP client."),
                    new DocumentedParameter("options", "The client options; the endpoint is required."),
                ]));
    }

    private static List<StatementSyntax> EmitRootAssignments(ClientPlan client, string? httpClientArgument)
    {
        var createArguments = new List<ArgumentSyntax>();
        if (httpClientArgument is not null)
        {
            createArguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(httpClientArgument)));
        }

        createArguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName("options")));
        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName("_pipeline"),
                EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("Pipeline"), "Create"),
                    [.. createArguments]))),
        };
        statements.AddRange(client.SubClients.Select(static reference => (StatementSyntax)SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(SubClientFieldName(reference)),
                SyntaxFactory.ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(reference.TypeName))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("_pipeline")))))))));
        return statements;
    }

    private static IEnumerable<MemberDeclarationSyntax> EmitPipelineConstructor(ClientPlan client,
        IReadOnlyList<StatementSyntax> assignments)
    {
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.ArgumentNullGuard(ReservedNamePolicy.PipelineParameter));
        statements.AddRange(assignments);
        yield return SyntaxFactory.ConstructorDeclaration(client.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.PipelineParameter)).WithType(TypeSyntaxEmitter.EmitNamed("Pipeline")))))
            .WithBody(SyntaxFactory.Block(statements));
    }

    private static IReadOnlyList<StatementSyntax> EmitPipelineAssignments(ClientPlan client)
    {
        _ = client;
        return
        [
            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName("_pipeline"),
                SyntaxFactory.IdentifierName(ReservedNamePolicy.PipelineParameter))),
        ];
    }

    private static IEnumerable<MemberDeclarationSyntax> EmitHandleConstructor(ClientPlan client)
    {
        var parameter = client.HandleParameter
                        ?? throw new InvalidOperationException($"Handle client '{client.Name}' carries no handle parameter.");
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.ArgumentNullGuard(ReservedNamePolicy.PipelineParameter));
        statements.AddRange(EmissionSyntax.RouteValueGuard(parameter.Name));
        statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName("_pipeline"),
            SyntaxFactory.IdentifierName(ReservedNamePolicy.PipelineParameter))));
        statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(HandleFieldName(parameter)),
            SyntaxFactory.IdentifierName(parameter.Name))));
        yield return SyntaxFactory.ConstructorDeclaration(client.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(ReservedNamePolicy.PipelineParameter)).WithType(TypeSyntaxEmitter.EmitNamed("Pipeline")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameter.Name))
                    .WithType(TypeSyntaxEmitter.EmitNamed(parameter.TypeName)),
            ])))
            .WithBody(SyntaxFactory.Block(statements));
    }

    private static ConstructorDeclarationSyntax EmitMockConstructor(ClientPlan client) =>
        SyntaxFactory.ConstructorDeclaration(client.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)))
            .WithBody(SyntaxFactory.Block())
            .WithLeadingTrivia(DocumentationSyntax.Documentation(
                "Initializes a mocking instance; members invoked without an override throw an instructive failure."));

    private static IEnumerable<MemberDeclarationSyntax> EmitDispose()
    {
        yield return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Dispose")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                    SyntaxFactory.IdentifierName("Dispose"),
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))
                        .WithNameColon(SyntaxFactory.NameColon("disposing")))),
                SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("GC"), "SuppressFinalize"),
                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression())))))
            .WithLeadingTrivia(DocumentationSyntax.Documentation("Releases the owned transport resources."));
        yield return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Dispose")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword),
                SyntaxFactory.Token(SyntaxKind.VirtualKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("disposing"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword))))))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.IfStatement(
                SyntaxFactory.IdentifierName("disposing"),
                SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(SyntaxFactory.ConditionalAccessExpression(
                    SyntaxFactory.IdentifierName("_pipeline"),
                    EmissionSyntax.Invocation(SyntaxFactory.MemberBindingExpression(SyntaxFactory.IdentifierName("Dispose")))))))))
            .WithLeadingTrivia(DocumentationSyntax.MemberDocumentation(
                "Releases the owned transport resources.",
                [new DocumentedParameter("disposing", "Whether managed state should be released.")]));
    }

    private static PropertyDeclarationSyntax EmitSubClientProperty(ClientPlan client, ClientReferencePlan reference) =>
        SyntaxFactory.PropertyDeclaration(TypeSyntaxEmitter.EmitNamed(reference.TypeName), reference.PropertyName)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(reference.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.VirtualKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                GuardedRead(client, SubClientFieldName(reference), reference.PropertyName)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(DocumentationSyntax.Documentation($"Gets the '{reference.PropertyName}' collection client."));

    private static MethodDeclarationSyntax EmitHandleFactory(ClientPlan client, HandleFactoryPlan factory)
    {
        var statements = new List<StatementSyntax>();
        statements.AddRange(EmissionSyntax.RouteValueGuard(factory.Parameter.Name));
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.ObjectCreationExpression(
                TypeSyntaxEmitter.EmitNamed(factory.HandleTypeName))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("Pipeline")),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(factory.Parameter.Name)),
            ])))));
        return SyntaxFactory.MethodDeclaration(TypeSyntaxEmitter.EmitNamed(factory.HandleTypeName), factory.MethodName)
            .WithModifiers(EmissionModifiers.Member(client.Emission))
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(client.Emission is EmissionMode.Public ? factory.ExperimentalDiagnostic : null))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(factory.Parameter.Name))
                    .WithType(TypeSyntaxEmitter.EmitNamed(factory.Parameter.TypeName)))))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(DocumentationSyntax.MemberDocumentation(
                $"Gets a bound '{factory.HandleTypeName}'; the handle never caches server state.",
                [new DocumentedParameter(factory.Parameter.Name, $"The '{factory.Parameter.WireName}' route value.")],
                $"The bound '{factory.HandleTypeName}'."));
    }

    private static PropertyDeclarationSyntax EmitGuardedAccessor(ClientPlan client, string propertyName, string typeName,
        string fieldName)
    {
        var declaration = SyntaxFactory.PropertyDeclaration(TypeSyntaxEmitter.EmitNamed(typeName), propertyName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(GuardedRead(client, fieldName, propertyName)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        return declaration;
    }

    private static BinaryExpressionSyntax GuardedRead(ClientPlan client, string fieldName, string memberName) =>
        SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            SyntaxFactory.IdentifierName(fieldName),
            SyntaxFactory.ThrowExpression(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("MockSeam"), "CreateError"),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(client.Name))),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(memberName))))));

    private static string SubClientFieldName(ClientReferencePlan reference) =>
        $"_{CSharpNamePolicy.ToCamelCase(reference.PropertyName)}";

    private static string HandleFieldName(OperationParameterPlan parameter) => $"_{parameter.Name}";
}
