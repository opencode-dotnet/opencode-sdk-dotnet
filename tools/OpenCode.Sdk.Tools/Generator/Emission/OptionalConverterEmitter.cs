using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits one closed converter per <c>Optional&lt;T&gt;</c> instantiation the models declare. The
/// wrapper cannot carry a type-level converter — the attribute argument would have to name an
/// unbound generic — so each flagged property points at a concrete converter instead, and
/// <c>HandleNull</c> is what lets a JSON null reach it as the explicit-null state rather than
/// short-circuiting to <c>default</c> (ADR-0004, ADR-0014).
/// </summary>
internal static class OptionalConverterEmitter
{
    /// <summary>The named types the emitters render without reaching into the generated models namespace.</summary>
    private static readonly string[] NonModelNames = ["bool", "double", "long", "string", "int", "JsonElement", "Uri"];

    private static readonly (string Literal, string Member)[] SpecialNumbers =
    [
        ("NaN", "NaN"),
        ("Infinity", "PositiveInfinity"),
        ("-Infinity", "NegativeInfinity"),
    ];

    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<ModelPlan> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var instantiations = models
            .OfType<ObjectModelPlan>()
            .SelectMany(static model => model.Properties)
            .Where(static property => property.EmitsOptionalWrapper)
            .Select(static property => property.Type)
            .DistinctBy(static type => (TypeReferenceNamePolicy.Format(type), OptionalConverterNamePolicy.ConverterTypeName(type)))
            .OrderBy(static type => OptionalConverterNamePolicy.ConverterTypeName(type), StringComparer.Ordinal);
        return Array.AsReadOnly([.. instantiations.Select(EmitConverter)]);
    }

    private static GeneratedSource EmitConverter(TypeReferencePlan inner)
    {
        var name = OptionalConverterNamePolicy.ConverterTypeName(inner);
        var wrapper = OptionalType(inner);
        var declaration = SyntaxFactory
            .ClassDeclaration(name)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic("JsonConverter", wrapper)))))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(
            [
                EmitHandleNull(),
                EmitRead(inner, wrapper),
                EmitWrite(inner, wrapper),
            ]));
        var unit = EmissionSyntax.CompilationUnit(GeneratedNamespace.Internal, CollectUsings(inner), [declaration]);
        return EmissionSyntax.CreateSource(GeneratedPlacement.PathOf(GeneratedKind.SharedSerialization, null, name), unit);
    }

    /// <summary><c>Optional&lt;T?&gt;</c>, the exact property type the record declares.</summary>
    private static GenericNameSyntax OptionalType(TypeReferencePlan inner) =>
        TypeSyntaxEmitter.Generic("Optional", TypeSyntaxEmitter.Emit(inner));

    /// <summary>Without this the serializer swallows a JSON null and the explicit-null state never arrives.</summary>
    private static PropertyDeclarationSyntax EmitHandleNull() => SyntaxFactory
        .PropertyDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "HandleNull")
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

    private static MethodDeclarationSyntax EmitRead(TypeReferencePlan inner, TypeSyntax wrapper)
    {
        var body = new List<StatementSyntax>();
        body.AddRange(EmissionSyntax.ArgumentNullGuard("typeToConvert"));
        body.AddRange(EmissionSyntax.ArgumentNullGuard("options"));
        body.Add(SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType"),
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), "Null")),
            SyntaxFactory.Block(SyntaxFactory.ReturnStatement(EmissionSyntax.MemberAccess(wrapper, "Null")))));
        if (inner is SpecialNumberTypeReferencePlan)
        {
            var literal = EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetString"));
            var number = SyntaxFactory.SwitchExpression(literal).WithArms(SyntaxFactory.SeparatedList(
                SpecialNumbers.Select(static number => SyntaxFactory.SwitchExpressionArm(
                    SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(number.Literal))),
                    EmissionSyntax.MemberAccess(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.DoubleKeyword)), number.Member)))
                .Append(SyntaxFactory.SwitchExpressionArm(SyntaxFactory.DiscardPattern(), SyntaxFactory.ThrowExpression(
                    SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName("JsonException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList()))))));
            body.Add(SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType"),
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), "String")),
                SyntaxFactory.Block(SyntaxFactory.ReturnStatement(Construct(wrapper, number)))));
        }

        body.AddRange(EmitReadValue(inner, wrapper));
        return SyntaxFactory
            .MethodDeclaration(wrapper, "Read")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("reader"))
                    .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader"))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("typeToConvert")).WithType(SyntaxFactory.IdentifierName("Type")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(SyntaxFactory.IdentifierName("JsonSerializerOptions")),
            ])))
            .WithBody(SyntaxFactory.Block(body));
    }

    private static IReadOnlyList<StatementSyntax> EmitReadValue(TypeReferencePlan inner, TypeSyntax wrapper)
    {
        if (ScalarReaderMethod(inner) is { } readerMethod)
        {
            return
            [
                SyntaxFactory.ReturnStatement(Construct(
                    wrapper,
                    EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), readerMethod)))),
            ];
        }

        var deserialize = SyntaxFactory.CastExpression(
            TypeSyntaxEmitter.Emit(inner),
            EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonSerializer"), "Deserialize"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader")).WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("typeInfo"))));
        return
        [
            TypeInfoLocal(inner),
            SyntaxFactory.ReturnStatement(Construct(wrapper, deserialize)),
        ];
    }

    private static MethodDeclarationSyntax EmitWrite(TypeReferencePlan inner, TypeSyntax wrapper)
    {
        var value = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "Value");
        var body = new List<StatementSyntax>();
        body.AddRange(EmissionSyntax.ArgumentNullGuard("writer"));
        body.AddRange(EmissionSyntax.ArgumentNullGuard("options"));
        body.Add(SyntaxFactory.IfStatement(
            SyntaxFactory.IsPatternExpression(
                value,
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
            SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), "WriteNullValue"))),
                SyntaxFactory.ReturnStatement())));
        if (inner is SpecialNumberTypeReferencePlan)
        {
            foreach (var number in SpecialNumbers)
            {
                body.Add(SyntaxFactory.IfStatement(EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.DoubleKeyword)), "Is" + number.Member),
                    SyntaxFactory.Argument(EmissionSyntax.MemberAccess(value, "Value"))), SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), "WriteStringValue"),
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(number.Literal))))),
                    SyntaxFactory.ReturnStatement())));
            }
        }

        body.AddRange(EmitWriteValue(inner, value));
        return SyntaxFactory
            .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), "Write")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("writer")).WithType(SyntaxFactory.IdentifierName("Utf8JsonWriter")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(wrapper),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(SyntaxFactory.IdentifierName("JsonSerializerOptions")),
            ])))
            .WithBody(SyntaxFactory.Block(body));
    }

    private static IReadOnlyList<StatementSyntax> EmitWriteValue(TypeReferencePlan inner, ExpressionSyntax value)
    {
        if (ScalarWriterMethod(inner) is { } writerMethod)
        {
            // A nullable value type reaches the writer through its own Value; a string is already
            // proven non-null by the guard above.
            var argument = inner is NamedTypeReferencePlan { Name: "string" } ? value : EmissionSyntax.MemberAccess(value, "Value");
            return
            [
                SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), writerMethod),
                    SyntaxFactory.Argument(argument))),
            ];
        }

        return
        [
            TypeInfoLocal(inner),
            SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonSerializer"), "Serialize"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("writer")),
                SyntaxFactory.Argument(value),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("typeInfo")))),
        ];
    }

    /// <summary>
    /// The inner type's metadata, bound the same way every other generated converter binds a known
    /// payload: through the emitted context, never through reflection.
    /// </summary>
    private static LocalDeclarationStatementSyntax TypeInfoLocal(TypeReferencePlan inner)
    {
        var get = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("OpenCodeJsonContext"), "Default"),
                "GetTypeInfo"),
            SyntaxFactory.Argument(SyntaxFactory.TypeOfExpression(TypeSyntaxEmitter.Emit(inner with { IsNullable = false }))));
        var resolved = SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            get,
            SyntaxFactory.ThrowExpression(SyntaxFactory
                .ObjectCreationExpression(SyntaxFactory.IdentifierName("JsonException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal("The generated context has no optional member metadata."))))))));
        return SyntaxFactory.LocalDeclarationStatement(SyntaxFactory
            .VariableDeclaration(SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory
                .VariableDeclarator("typeInfo")
                .WithInitializer(SyntaxFactory.EqualsValueClause(resolved)))));
    }

    private static ObjectCreationExpressionSyntax Construct(TypeSyntax wrapper, ExpressionSyntax value) => SyntaxFactory
        .ObjectCreationExpression(wrapper)
        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(value))));

    private static string? ScalarReaderMethod(TypeReferencePlan plan) => plan switch
    {
        NamedTypeReferencePlan { Name: "string" } => "GetString",
        NamedTypeReferencePlan { Name: "bool" } => "GetBoolean",
        NamedTypeReferencePlan { Name: "long" } => "GetInt64",
        NamedTypeReferencePlan { Name: "double" } => "GetDouble",
        SpecialNumberTypeReferencePlan => "GetDouble",
        _ => null,
    };

    private static string? ScalarWriterMethod(TypeReferencePlan plan) => plan switch
    {
        NamedTypeReferencePlan { Name: "string" } => "WriteStringValue",
        NamedTypeReferencePlan { Name: "bool" } => "WriteBooleanValue",
        NamedTypeReferencePlan { Name: "long" or "double" } => "WriteNumberValue",
        SpecialNumberTypeReferencePlan => "WriteNumberValue",
        _ => null,
    };

    private static IReadOnlyList<string> CollectUsings(TypeReferencePlan inner)
    {
        var result = new HashSet<string>(StringComparer.Ordinal)
        {
            "System",
            "System.Text.Json",
            "System.Text.Json.Serialization",
        };
        TypeUsingCollector.Collect(inner, result);
        if (ReferencesGeneratedModel(inner))
        {
            _ = result.Add(GeneratedNamespace.Models);
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }

    private static bool ReferencesGeneratedModel(TypeReferencePlan plan) => plan switch
    {
        NamedTypeReferencePlan named => !NonModelNames.Contains(named.Name, StringComparer.Ordinal),
        ListTypeReferencePlan list => ReferencesGeneratedModel(list.ElementType),
        DictionaryTypeReferencePlan dictionary => ReferencesGeneratedModel(dictionary.ValueType),
        _ => false,
    };
}
