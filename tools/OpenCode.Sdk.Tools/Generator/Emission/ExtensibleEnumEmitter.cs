using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Emits a string enum of the pinned document as an extensible, string-backed readonly struct
/// and the converter it carries: the pinned values are static properties, any other
/// wire string a newer server sends is kept as it arrived and written back unchanged, and equality
/// compares spellings ordinally, as upstream compares string literals. A <see langword="default"/>
/// value has no spelling and the converter refuses to write it.
/// </summary>
internal static class ExtensibleEnumEmitter
{
    private const string ValueField = "_value";
    private const string ValueParameter = "value";

    public static IReadOnlyList<GeneratedSource> Emit(EnumModelPlan model, ModelPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(placement);

        var converterName = ConverterName(model.Name);
        return
        [
            EmitStruct(model, converterName, GeneratedPlacement.ModelPathOf(placement, model.Name)),
            EmitConverter(model, converterName, GeneratedPlacement.ConverterPathOf(placement, model.Name, converterName)),
        ];
    }

    /// <summary>Names the converter an enum carries.</summary>
    public static string ConverterName(string enumName) => $"{enumName}JsonConverter";

    private static GeneratedSource EmitStruct(EnumModelPlan model, string converterName, string relativePath)
    {
        var type = TypeSyntaxEmitter.EmitNamed(model.Name);
        List<MemberDeclarationSyntax> members =
        [
            ValueFieldDeclaration(),
            Constructor(model),
            .. model.Values.Select(value => KnownValueProperty(model, value)),
            IsKnownProperty(model),
            EqualityOperator(type, SyntaxKind.EqualsEqualsToken, negate: false),
            EqualityOperator(type, SyntaxKind.ExclamationEqualsToken, negate: true),
            ImplicitConversion(model, type),
            FromString(model, type),
            TypedEquals(type),
            ObjectEquals(type),
            HashCode(),
            SpellingToString(),
        ];

        var declaration = SyntaxFactory
            .StructDeclaration(model.Name)
            .AddAttributeLists(ExperimentalAttributeSyntax.Emit(model.ExperimentalDiagnostic))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic("IEquatable", type)))))
            .AddAttributeLists(EmissionSyntax.Attribute("JsonConverter", SyntaxFactory.AttributeArgument(
                SyntaxFactory.TypeOfExpression(TypeSyntaxEmitter.EmitNamed(converterName)))))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(EmissionSyntax.Documentation(StructSummary(model)));
        declaration = EmissionSyntax.DocumentedType(declaration, model.Documentation);
        var unit = EmissionSyntax.CompilationUnit(
            model.Namespace,
            ["System", "System.Text.Json.Serialization", GeneratedNamespace.Internal],
            [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }

    private static string StructSummary(EnumModelPlan model)
    {
        var description = model.Description ?? $"The {GeneratedDisplayName.Of(model.Name)}.";
        return $"{description} The pinned server's values are the static properties; a value a newer server sends is kept as it arrived, and {ExtensibleEnumMemberPolicy.IsKnown} tells the two apart.";
    }

    private static FieldDeclarationSyntax ValueFieldDeclaration() => SyntaxFactory
        .FieldDeclaration(SyntaxFactory
            .VariableDeclaration(NullableString())
            .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(ValueField))))
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
            SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static ConstructorDeclarationSyntax Constructor(EnumModelPlan model) => SyntaxFactory
        .ConstructorDeclaration(model.Name)
        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
        .WithParameterList(StringParameter())
        .WithBody(SyntaxFactory.Block(SyntaxFactory.List(
        [
            .. EmissionSyntax.ArgumentNullGuard(ValueParameter),
            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(ValueField),
                SyntaxFactory.IdentifierName(ValueParameter))),
        ])))
        .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
            "Creates a value from its wire spelling; any spelling is accepted, pinned or not.",
            [new DocumentedParameter(ValueParameter, "The wire spelling.")]));

    private static PropertyDeclarationSyntax KnownValueProperty(EnumModelPlan model, EnumValuePlan value) => SyntaxFactory
        .PropertyDeclaration(TypeSyntaxEmitter.EmitNamed(model.Name), value.Name)
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
        .WithAccessorList(GetOnly())
        .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory
            .ImplicitObjectCreationExpression()
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(StringLiteral(value.WireValue)))))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(EmissionSyntax.Documentation($"Gets the '{value.WireValue}' wire value."));

    private static PropertyDeclarationSyntax IsKnownProperty(EnumModelPlan model)
    {
        var pinned = model.Values
            .Select(static value => (PatternSyntax)SyntaxFactory.ConstantPattern(StringLiteral(value.WireValue)))
            .Aggregate((left, right) => SyntaxFactory.BinaryPattern(SyntaxKind.OrPattern, left, right));
        return SyntaxFactory
            .PropertyDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), ExtensibleEnumMemberPolicy.IsKnown)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.IsPatternExpression(SyntaxFactory.IdentifierName(ValueField), pinned)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                "Gets whether the value is one the pinned server defines; a newer SDK can know a value this one does not."));
    }

    private static OperatorDeclarationSyntax EqualityOperator(TypeSyntax type, SyntaxKind operatorToken, bool negate)
    {
        ExpressionSyntax equals = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("left"), "Equals"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("right")));
        return SyntaxFactory
            .OperatorDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), SyntaxFactory.Token(operatorToken))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("left")).WithType(type),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("right")).WithType(type),
            ])))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(negate
                ? SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, equals)
                : equals))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
                negate ? "Compares two values' wire spellings ordinally for inequality." : "Compares two values' wire spellings ordinally.",
                [new DocumentedParameter("left", "The first value."), new DocumentedParameter("right", "The second value.")]));
    }

    private static ConversionOperatorDeclarationSyntax ImplicitConversion(EnumModelPlan model, TypeSyntax type) => SyntaxFactory
        .ConversionOperatorDeclaration(SyntaxFactory.Token(SyntaxKind.ImplicitKeyword), type)
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
        .WithParameterList(StringParameter())
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(NewFromParameter(model)))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
            "Converts a wire spelling to a value.",
            [new DocumentedParameter(ValueParameter, "The wire spelling.")]));

    /// <summary>The named alternate of the implicit conversion (CA2225).</summary>
    private static MethodDeclarationSyntax FromString(EnumModelPlan model, TypeSyntax type) => SyntaxFactory
        .MethodDeclaration(type, ExtensibleEnumMemberPolicy.FromString)
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
        .WithParameterList(StringParameter())
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(NewFromParameter(model)))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
            "Creates a value from its wire spelling, as the implicit conversion does.",
            [new DocumentedParameter(ValueParameter, "The wire spelling.")]));

    private static MethodDeclarationSyntax TypedEquals(TypeSyntax type) => SyntaxFactory
        .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "Equals")
        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("other")).WithType(type))))
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)), "Equals"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ValueField)),
            SyntaxFactory.Argument(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("other"), ValueField)),
            SyntaxFactory.Argument(OrdinalComparison()))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
            "Compares wire spellings ordinally: upstream compares string literals exactly.",
            [new DocumentedParameter("other", "The other value.")]));

    private static MethodDeclarationSyntax ObjectEquals(TypeSyntax type) => SyntaxFactory
        .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "Equals")
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("obj"))
                .WithType(SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))))))
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("obj"),
                SyntaxFactory.DeclarationPattern(type, SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other")))),
            EmissionSyntax.Invocation(SyntaxFactory.IdentifierName("Equals"), SyntaxFactory.Argument(SyntaxFactory.IdentifierName("other"))))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(EmissionSyntax.MemberDocumentation(
            "Compares with another object: equal only to a value with the same wire spelling.",
            [new DocumentedParameter("obj", "The other object.")]));

    private static MethodDeclarationSyntax HashCode() => SyntaxFactory
        .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)), "GetHashCode")
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
        .WithParameterList(SyntaxFactory.ParameterList())
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.ConditionalExpression(
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName(ValueField),
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)),
            EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StringComparer"), "Ordinal"),
                    "GetHashCode"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ValueField))))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(EmissionSyntax.Documentation("Hashes the wire spelling ordinally, as equality compares it."));

    private static MethodDeclarationSyntax SpellingToString() => SyntaxFactory
        .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)), "ToString")
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
        .WithParameterList(SyntaxFactory.ParameterList())
        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            SyntaxFactory.IdentifierName(ValueField),
            EmissionSyntax.MemberAccess(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)), "Empty"))))
        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
        .WithLeadingTrivia(EmissionSyntax.Documentation(
            "Returns the wire spelling, or an empty string for a default value, which has none."));

    /// <summary>
    /// The converter: any string token reads as a value, pinned or not; any other token is a
    /// malformed body, so a number never reads as a value; a default value has no spelling and is
    /// refused rather than written as an empty string.
    /// </summary>
    private static GeneratedSource EmitConverter(EnumModelPlan model, string converterName, string relativePath)
    {
        var type = TypeSyntaxEmitter.EmitNamed(model.Name);
        var reader = SyntaxFactory.IdentifierName("reader");
        var read = SyntaxFactory
            .MethodDeclaration(type, "Read")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("reader"))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)))
                    .WithType(TypeSyntaxEmitter.EmitNamed("Utf8JsonReader")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("typeToConvert")).WithType(TypeSyntaxEmitter.EmitNamed("Type")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(TypeSyntaxEmitter.EmitNamed("JsonSerializerOptions")),
            ])))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.IfStatement(
                    SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        EmissionSyntax.MemberAccess(reader, "TokenType"),
                        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), "String")),
                    SyntaxFactory.Block(SyntaxFactory.ThrowStatement(JsonException(SyntaxFactory.InterpolatedStringExpression(
                        SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
                        SyntaxFactory.List<InterpolatedStringContentSyntax>(
                        [
                            InterpolatedText($"Expected a string for {model.Name}, got "),
                            SyntaxFactory.Interpolation(EmissionSyntax.MemberAccess(reader, "TokenType")),
                            InterpolatedText("."),
                        ])))))),
                SyntaxFactory.ReturnStatement(SyntaxFactory
                    .ObjectCreationExpression(type)
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                        SyntaxFactory.PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(reader, "GetString"))))))))));
        var write = SyntaxFactory
            .MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), "Write")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("writer")).WithType(TypeSyntaxEmitter.EmitNamed("Utf8JsonWriter")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(ValueParameter)).WithType(type),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(TypeSyntaxEmitter.EmitNamed("JsonSerializerOptions")),
            ])))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.List(
            [
                .. EmissionSyntax.ArgumentNullGuard("writer"),
                SyntaxFactory.IfStatement(
                    EmissionSyntax.Invocation(
                        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName(ValueParameter), "Equals"),
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.DefaultLiteralExpression,
                            SyntaxFactory.Token(SyntaxKind.DefaultKeyword)))),
                    SyntaxFactory.Block(SyntaxFactory.ThrowStatement(JsonException(
                        StringLiteral($"A default {model.Name} has no wire value."))))),
                SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), "WriteStringValue"),
                    SyntaxFactory.Argument(EmissionSyntax.Invocation(
                        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName(ValueParameter), "ToString"))))),
            ])));
        var declaration = SyntaxFactory
            .ClassDeclaration(converterName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic("JsonConverter", type)))))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>([read, write]))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                $"Converts {model.Name}: every string token reads as the value it spells, pinned or not; any other token is a malformed body. Writes the wire spelling and refuses a default value, which has none."));
        var unit = EmissionSyntax.CompilationUnit(
            GeneratedNamespace.Internal,
            ["System", "System.Text.Json", "System.Text.Json.Serialization", model.Namespace],
            [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }

    private static ObjectCreationExpressionSyntax NewFromParameter(EnumModelPlan model) => SyntaxFactory
        .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(model.Name))
        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(ValueParameter)))));

    private static ObjectCreationExpressionSyntax JsonException(ExpressionSyntax message) => SyntaxFactory
        .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed("JsonException"))
        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(message))));

    private static InterpolatedStringTextSyntax InterpolatedText(string text) => SyntaxFactory.InterpolatedStringText(
        SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.InterpolatedStringTextToken, text, text, SyntaxFactory.TriviaList()));

    private static ParameterListSyntax StringParameter() => SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
        SyntaxFactory.Parameter(SyntaxFactory.Identifier(ValueParameter))
            .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))));

    private static NullableTypeSyntax NullableString() =>
        SyntaxFactory.NullableType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)));

    private static MemberAccessExpressionSyntax OrdinalComparison() =>
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StringComparison"), "Ordinal");

    private static AccessorListSyntax GetOnly() => SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))));

    private static LiteralExpressionSyntax StringLiteral(string value) =>
        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));
}
