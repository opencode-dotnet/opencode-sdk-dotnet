using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class StructuralUnionEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<StructuralUnionModelPlan> unions)
    {
        ArgumentNullException.ThrowIfNull(unions);

        var result = new List<GeneratedSource>(unions.Count * 3);
        foreach (var union in unions.OrderBy(static union => union.Name, StringComparer.Ordinal))
        {
            result.Add(EmitKind(union));
            result.Add(EmitCarrier(union));
            result.Add(EmitConverter(union));
        }

        return Array.AsReadOnly([.. result]);
    }

    private static GeneratedSource EmitKind(StructuralUnionModelPlan union)
    {
        var members = union
            .Arms.Select(static arm => arm.Name)
            .Append("Unknown")
            .Select(name => SyntaxFactory
                .EnumMemberDeclaration(name)
                .WithLeadingTrivia(EmissionSyntax.Documentation($"Represents the {DisplayName(name)} arm.")));
        var declaration = SyntaxFactory
            .EnumDeclaration(union.KindTypeName)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithMembers(SyntaxFactory.SeparatedList(members))
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Identifies the active arm of {union.Name}."));
        var unit = EmissionSyntax.CompilationUnit(union.Namespace, [], [declaration]);
        return EmissionSyntax.CreateSource($"Models/{union.KindTypeName}.cs", unit);
    }

    private static GeneratedSource EmitCarrier(StructuralUnionModelPlan union)
    {
        var members = new List<MemberDeclarationSyntax>
        {
            EmitValueField(),
            EmitConstructor(union),
            EmitKindProperty(union),
        };
        foreach (var arm in union.Arms)
        {
            members.Add(EmitArmProperty(union, arm));
            members.Add(EmitFactory(union, arm));
        }

        members.Add(EmitUnknownProperty(union));
        members.Add(EmitUnknownFactory(union));
        members.Add(EmitValueGetter(union));
        members.Add(EmitToString(union));

        var declaration = SyntaxFactory
            .RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), union.Name)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
            .AddAttributeLists(EmissionSyntax.Attribute(
                "JsonConverter",
                SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(
                    TypeSyntaxEmitter.EmitNamed($"{union.Name}JsonConverter")))))
            .WithMembers(SyntaxFactory.List(members))
            .WithLeadingTrivia(EmissionSyntax.Documentation(
                union.Description ?? $"Represents a {DisplayName(union.Name)} structural union."));
        var unit = EmissionSyntax.CompilationUnit(
            union.Namespace,
            [
                "OpenCode.Sdk.Internal.Serialization",
                "System",
                "System.Collections.Generic",
                "System.Text.Json",
                "System.Text.Json.Serialization",
            ],
            [declaration]);
        return EmissionSyntax.CreateSource($"Models/{union.Name}.cs", unit);
    }

    /// <summary>
    /// A record's compiler-synthesized ToString prints every public property through PrintMembers,
    /// and an inactive arm throws by design. The union overrides ToString itself instead of
    /// declaring PrintMembers: a sealed record with no record base may only declare that hook
    /// private (CS8879), where IDE0051 cannot see the synthesized caller and the writer's format
    /// pass would strip it. The kind enum's member names are the arm names, so Kind.ToString()
    /// names the active arm for the printer.
    /// </summary>
    /// <remarks>
    /// When a known arm carries a secret member, a token this pin does not recognize may carry
    /// one too, so the Unknown arm prints the redaction marker in place of its preserved text.
    /// </remarks>
    private static MethodDeclarationSyntax EmitToString(StructuralUnionModelPlan union)
    {
        ExpressionSyntax value = SyntaxFactory.IdentifierName("_value");
        if (union.MasksUnknownPayload)
        {
            value = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.IsPatternExpression(
                    SyntaxFactory.IdentifierName("Kind"),
                    SyntaxFactory.ConstantPattern(KindMember(union, "Unknown"))),
                RecordPrinterSyntax.Redacted(),
                value);
        }

        return RecordPrinterSyntax.ToStringOverride(
            EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StructuralUnionPrinter"), "Format"),
                SyntaxFactory.Argument(RecordPrinterSyntax.NameOf(union.Name)),
                SyntaxFactory.Argument(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("Kind"), "ToString"))),
                SyntaxFactory.Argument(value)),
            union.MasksUnknownPayload
                ? "Prints the kind and the active arm, an unrecognized token masked; the inactive arms throw by design."
                : "Prints the kind and the active arm; the inactive arms throw by design.");
    }

    private static FieldDeclarationSyntax EmitValueField() => SyntaxFactory
        .FieldDeclaration(
            SyntaxFactory
                .VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("_value"))))
        .WithModifiers(SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
            SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)));

    private static ConstructorDeclarationSyntax EmitConstructor(StructuralUnionModelPlan union) =>
        SyntaxFactory
            .ConstructorDeclaration(union.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("kind"))
                    .WithType(TypeSyntaxEmitter.EmitNamed(union.KindTypeName)),
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("value"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))),
            ])))
            .WithBody(SyntaxFactory.Block(
                Assign("Kind", "kind"),
                Assign("_value", "value")));

    private static ExpressionStatementSyntax Assign(string target, string value) => SyntaxFactory.ExpressionStatement(
        SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(target),
            SyntaxFactory.IdentifierName(value)));

    private static PropertyDeclarationSyntax EmitKindProperty(StructuralUnionModelPlan union) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.EmitNamed(union.KindTypeName), "Kind")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(GetOnlyAccessors())
            .WithLeadingTrivia(EmissionSyntax.Documentation("Gets the active structural union arm."));

    private static PropertyDeclarationSyntax EmitArmProperty(StructuralUnionModelPlan union, StructuralUnionArmPlan arm) =>
        SyntaxFactory
            .PropertyDeclaration(TypeSyntaxEmitter.Emit(arm.Type), arm.Name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.Invocation(
                SyntaxFactory
                    .GenericName("GetValue")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(TypeSyntaxEmitter.Emit(arm.Type)))),
                SyntaxFactory.Argument(KindMember(union, arm.Name)))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Gets the {DisplayName(arm.Name)} value."));

    private static MethodDeclarationSyntax EmitFactory(StructuralUnionModelPlan union, StructuralUnionArmPlan arm)
    {
        var statements = new List<StatementSyntax>();
        if (IsReferenceType(arm.Type))
        {
            statements.AddRange(EmissionSyntax.ArgumentNullGuard("value"));
        }

        statements.Add(SyntaxFactory.ReturnStatement(NewCarrier(union, arm.Name, SyntaxFactory.IdentifierName("value"))));
        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), $"From{arm.Name}")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(TypeSyntaxEmitter.Emit(arm.Type)))))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(EmissionSyntax.Documentation($"Creates a {DisplayName(union.Name)} from its {DisplayName(arm.Name)} arm."));
    }

    private static bool IsReferenceType(TypeReferencePlan type) => type switch
    {
        ListTypeReferencePlan or DictionaryTypeReferencePlan => true,
        NamedTypeReferencePlan { Name: "bool" or "double" or "long" } => false,
        SpecialNumberTypeReferencePlan => false,
        BinaryTypeReferencePlan => false,
        NamedTypeReferencePlan => true,
        _ => true,
    };

    private static PropertyDeclarationSyntax EmitUnknownProperty(StructuralUnionModelPlan union) =>
        SyntaxFactory
            .PropertyDeclaration(SyntaxFactory.IdentifierName("JsonElement"), "Unknown")
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(EmissionSyntax.Invocation(
                SyntaxFactory
                    .GenericName("GetValue")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName("JsonElement")))),
                SyntaxFactory.Argument(KindMember(union, "Unknown")))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(EmissionSyntax.Documentation("Gets the preserved raw payload for an unrecognized token kind."));

    private static MethodDeclarationSyntax EmitUnknownFactory(StructuralUnionModelPlan union)
    {
        var valueKind = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "ValueKind");
        var undefined = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonValueKind"), "Undefined");
        var guard = SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, valueKind, undefined),
            SyntaxFactory.ThrowStatement(SyntaxFactory
                .ObjectCreationExpression(SyntaxFactory.IdentifierName("ArgumentException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                [
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal("The unknown value must be a parsed JSON element."))),
                    SyntaxFactory.Argument(EmissionSyntax.Invocation(
                        SyntaxFactory.IdentifierName("nameof"),
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("value")))),
                ])))));
        var clone = EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "Clone"));
        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), "FromUnknown")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(SyntaxFactory.IdentifierName("JsonElement")))))
            .WithBody(SyntaxFactory.Block(
                guard,
                SyntaxFactory.ReturnStatement(NewCarrier(union, "Unknown", clone))))
            .WithLeadingTrivia(EmissionSyntax.Documentation("Creates a structural union value preserving an unrecognized JSON token."));
    }

    private static ObjectCreationExpressionSyntax NewCarrier(StructuralUnionModelPlan union, string armName, ExpressionSyntax value) =>
        SyntaxFactory
            .ObjectCreationExpression(TypeSyntaxEmitter.EmitNamed(union.Name))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Argument(KindMember(union, armName)),
                SyntaxFactory.Argument(value),
            ])));

    private static MethodDeclarationSyntax EmitValueGetter(StructuralUnionModelPlan union)
    {
        var mismatch = SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            SyntaxFactory.IdentifierName("Kind"),
            SyntaxFactory.IdentifierName("expected"));
        return SyntaxFactory
            .MethodDeclaration(SyntaxFactory.IdentifierName("T"), "GetValue")
            .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.TypeParameter("T"))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("expected"))
                    .WithType(TypeSyntaxEmitter.EmitNamed(union.KindTypeName)))))
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.IfStatement(
                    mismatch,
                    SyntaxFactory.ThrowStatement(SyntaxFactory
                        .ObjectCreationExpression(
                            SyntaxFactory.IdentifierName("InvalidOperationException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal("The structural value does not contain the requested arm.")))))))),
                SyntaxFactory.ReturnStatement(SyntaxFactory.CastExpression(
                    SyntaxFactory.IdentifierName("T"),
                    SyntaxFactory.IdentifierName("_value")))));
    }

    private static AccessorListSyntax GetOnlyAccessors() => SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
        SyntaxFactory
            .AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))));

    private static MemberAccessExpressionSyntax KindMember(StructuralUnionModelPlan union, string name) =>
        EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.KindTypeName), name);

    private static GeneratedSource EmitConverter(StructuralUnionModelPlan union)
    {
        var declaration = SyntaxFactory
            .ClassDeclaration($"{union.Name}JsonConverter")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.InternalKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)))
            .WithBaseList(SyntaxFactory.BaseList(SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(
                SyntaxFactory.SimpleBaseType(TypeSyntaxEmitter.Generic(
                    "JsonConverter",
                    TypeSyntaxEmitter.EmitNamed(union.Name))))))
            .WithMembers(SyntaxFactory.List(ConverterMembers(union)));
        var unit = EmissionSyntax.CompilationUnit(
            "OpenCode.Sdk.Internal.Serialization",
            [
                "OpenCode.Sdk.Models",
                "System",
                "System.Text.Json",
                "System.Text.Json.Serialization",
            ],
            [declaration]);
        return EmissionSyntax.CreateSource($"Internal/Serialization/{union.Name}JsonConverter.cs", unit);
    }

    private static IEnumerable<MemberDeclarationSyntax> ConverterMembers(StructuralUnionModelPlan union)
    {
        var firstMatch = union.Arms.Any(static arm => arm.Claim is not null);
        yield return EmitRead(union);
        yield return EmitWrite(union);
        if (firstMatch)
        {
            yield return EmitReadObject(union);
        }

        yield return EmitReadKnown(fromElement: false);
        if (firstMatch)
        {
            yield return EmitReadKnown(fromElement: true);
        }

        yield return EmitWriteKnown();
        yield return EmitReadUnknown(union);
    }

    private static MethodDeclarationSyntax EmitRead(StructuralUnionModelPlan union)
    {
        var claimedTokens = union.Arms.SelectMany(static arm => arm.Tokens).ToHashSet();
        var firstClaimed = union.Arms.FirstOrDefault(static arm => arm.Claim is not null);
        var arms = union
            .Arms.SelectMany(arm => ArmTokenArms(union, arm, firstClaimed))
            .Concat(Enum
                .GetValues<JsonTokenType>()
                .Where(token => !claimedTokens.Contains(token))
                .Select(token => SyntaxFactory.SwitchExpressionArm(
                    SyntaxFactory.ConstantPattern(EmissionSyntax.MemberAccess(
                        SyntaxFactory.IdentifierName("JsonTokenType"), token.ToString())),
                    IsValueStartToken(token)
                        ? EmissionSyntax.Invocation(
                            SyntaxFactory.IdentifierName("ReadUnknown"),
                            SyntaxFactory
                                .Argument(SyntaxFactory.IdentifierName("reader"))
                                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)))
                        : SyntaxFactory.ThrowExpression(JsonException(
                            $"JSON token '{token}' cannot start this structural union value.")))))
            .Append(SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.DiscardPattern(),
                SyntaxFactory.ThrowExpression(JsonException("The JSON token kind is not recognized."))))
            .ToArray();
        var tokenType = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType");
        var body = new List<StatementSyntax>();
        body.AddRange(EmissionSyntax.ArgumentNullGuard("typeToConvert"));
        body.AddRange(EmissionSyntax.ArgumentNullGuard("options"));
        body.Add(SyntaxFactory.ReturnStatement(SyntaxFactory
            .SwitchExpression(tokenType)
            .WithArms(SyntaxFactory.SeparatedList(arms))));
        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), "Read")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(ConverterReadParameters())
            .WithBody(SyntaxFactory.Block(body));
    }

    private static bool IsValueStartToken(JsonTokenType token) => token is
        JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.String
        or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False;

    private static ExpressionSyntax EmitReadValue(TypeReferencePlan type) => type switch
    {
        NamedTypeReferencePlan { Name: "string" } => SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetString")),
            SyntaxFactory.ThrowExpression(JsonException("The string arm materialized null."))),
        NamedTypeReferencePlan { Name: "double" } =>
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetDouble")),
        NamedTypeReferencePlan { Name: "long" } =>
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetInt64")),
        NamedTypeReferencePlan { Name: "bool" } =>
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetBoolean")),
        SpecialNumberTypeReferencePlan =>
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetDouble")),
        _ => EmissionSyntax.Invocation(
            SyntaxFactory
                .GenericName("ReadKnown")
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(TypeSyntaxEmitter.Emit(type)))),
            SyntaxFactory
                .Argument(SyntaxFactory.IdentifierName("reader"))
                .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword))),
    };

    private static MethodDeclarationSyntax EmitWrite(StructuralUnionModelPlan union)
    {
        var sections = union
            .Arms.Select(arm => SyntaxFactory
                .SwitchSection()
                .WithLabels(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(KindMember(union, arm.Name))))
                .WithStatements(SyntaxFactory.List<StatementSyntax>(
                [
                    SyntaxFactory.ExpressionStatement(EmitWriteValue(arm)),
                    SyntaxFactory.BreakStatement(),
                ])))
            .Append(SyntaxFactory
                .SwitchSection()
                .WithLabels(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(KindMember(union, "Unknown"))))
                .WithStatements(SyntaxFactory.List<StatementSyntax>(
                [
                    SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(
                        EmissionSyntax.MemberAccess(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "Unknown"), "WriteTo"),
                        SyntaxFactory.Argument(SyntaxFactory.IdentifierName("writer")))),
                    SyntaxFactory.BreakStatement(),
                ])))
            .Append(SyntaxFactory
                .SwitchSection()
                .WithLabels(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.DefaultSwitchLabel()))
                .WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ThrowStatement(
                    JsonException("The structural union kind is not recognized.")))))
            .ToArray();
        var body = new List<StatementSyntax>();
        body.AddRange(EmissionSyntax.ArgumentNullGuard("writer"));
        body.AddRange(EmissionSyntax.ArgumentNullGuard("value"));
        body.AddRange(EmissionSyntax.ArgumentNullGuard("options"));
        body.Add(SyntaxFactory
            .SwitchStatement(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "Kind"))
            .WithSections(SyntaxFactory.List(sections)));
        return SyntaxFactory
            .MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Write")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(ConverterWriteParameters(union))
            .WithBody(SyntaxFactory.Block(body));
    }

    private static InvocationExpressionSyntax EmitWriteValue(StructuralUnionArmPlan arm)
    {
        var value = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), arm.Name);
        return arm.Type switch
        {
            NamedTypeReferencePlan { Name: "string" } => EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), "WriteStringValue"),
                SyntaxFactory.Argument(value)),
            NamedTypeReferencePlan { Name: "double" } or SpecialNumberTypeReferencePlan => EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), "WriteNumberValue"),
                SyntaxFactory.Argument(value)),
            NamedTypeReferencePlan { Name: "long" } => EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), "WriteNumberValue"),
                SyntaxFactory.Argument(value)),
            NamedTypeReferencePlan { Name: "bool" } => EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("writer"), "WriteBooleanValue"),
                SyntaxFactory.Argument(value)),
            _ => EmissionSyntax.Invocation(
                SyntaxFactory
                    .GenericName("WriteKnown")
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(TypeSyntaxEmitter.Emit(arm.Type)))),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("writer")),
                SyntaxFactory.Argument(value)),
        };
    }

    /// <summary>
    /// An arm its token alone selects reads its value directly; the object arms that share the
    /// object token route it once, from the first of them, to the first-match read.
    /// </summary>
    private static IEnumerable<SwitchExpressionArmSyntax> ArmTokenArms(StructuralUnionModelPlan union, StructuralUnionArmPlan arm,
        StructuralUnionArmPlan? firstClaimed)
    {
        if (arm.Claim is null)
        {
            return arm.Tokens.Select(token => TokenArm(token, EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.Name), $"From{arm.Name}"),
                SyntaxFactory.Argument(EmitReadValue(arm.Type)))));
        }

        return ReferenceEquals(arm, firstClaimed)
            ? [TokenArm(JsonTokenType.StartObject, EmissionSyntax.Invocation(SyntaxFactory.IdentifierName("ReadObject"), ReaderArgument()))]
            : [];
    }

    private static SwitchExpressionArmSyntax TokenArm(JsonTokenType token, ExpressionSyntax value) =>
        SyntaxFactory.SwitchExpressionArm(
            SyntaxFactory.ConstantPattern(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), token.ToString())),
            value);

    private static ArgumentSyntax ReaderArgument() => SyntaxFactory
        .Argument(SyntaxFactory.IdentifierName("reader"))
        .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword));

    /// <summary>
    /// Emits the first-match read of the object arms that share the object token: the value is
    /// buffered once, each arm's claim is tried in declaration order, the first that holds
    /// deep-parses the value as that arm, and a value no claim holds stays raw as <c>Unknown</c>.
    /// </summary>
    private static MethodDeclarationSyntax EmitReadObject(StructuralUnionModelPlan union)
    {
        var parse = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonDocument"), "ParseValue"),
            ReaderArgument());
        var body = new List<StatementSyntax>
        {
            SyntaxFactory
                .LocalDeclarationStatement(SyntaxFactory
                    .VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("document").WithInitializer(SyntaxFactory.EqualsValueClause(parse)))))
                .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword)),
            Local("root", EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("document"), "RootElement")),
        };
        var unconditional = false;
        foreach (var arm in union.Arms.Where(static arm => arm.Claim is not null))
        {
            var select = SyntaxFactory.ReturnStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.Name), $"From{arm.Name}"),
                SyntaxFactory.Argument(EmissionSyntax.Invocation(
                    SyntaxFactory
                        .GenericName("ReadKnown")
                        .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SingletonSeparatedList(TypeSyntaxEmitter.Emit(arm.Type)))),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("root"))))));
            var conditions = ClaimConditions(arm.Claim!).ToArray();
            if (conditions.Length is 0)
            {
                // A claim with nothing to check takes every object; the binder refuses any arm
                // after it, so it is the last one tried.
                body.Add(select);
                unconditional = true;
                break;
            }

            var condition = conditions.Aggregate(static (left, right) =>
                SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, left, right));
            body.Add(SyntaxFactory.IfStatement(condition, SyntaxFactory.Block(select)));
        }

        if (!unconditional)
        {
            body.Add(SyntaxFactory.ReturnStatement(EmissionSyntax.Invocation(
                EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.Name), "FromUnknown"),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("root")))));
        }

        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), "ReadObject")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("reader"))
                    .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader"))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))))))
            .WithBody(SyntaxFactory.Block(body));
    }

    /// <summary>
    /// A required key with a sentinel must hold one of its values, a required key without one must
    /// be present, and an optional sentinel must hold one of its values when it is present.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> ClaimConditions(StructuralObjectClaimPlan claim)
    {
        foreach (var requiredKey in claim.RequiredKeys)
        {
            var sentinel = claim.Sentinels.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Property, requiredKey));
            if (sentinel is null)
            {
                yield return ClaimCall("Has", requiredKey, []);
                continue;
            }

            yield return ClaimCall(sentinel.Kind is LiteralKind.Boolean ? "IsBoolean" : "IsText", requiredKey, SentinelValues(sentinel));
        }

        foreach (var sentinel in claim.Sentinels.Where(candidate => !claim.RequiredKeys.Contains(candidate.Property, StringComparer.Ordinal)))
        {
            yield return ClaimCall(sentinel.Kind is LiteralKind.Boolean ? "AllowsBoolean" : "AllowsText", sentinel.Property, SentinelValues(sentinel));
        }
    }

    private static InvocationExpressionSyntax ClaimCall(string method, string property, IEnumerable<ExpressionSyntax> values) =>
        EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("StructuralUnionClaim"), method),
            [
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("root")),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(property))),
                .. values.Select(SyntaxFactory.Argument),
            ]);

    private static IEnumerable<ExpressionSyntax> SentinelValues(StructuralSentinelPlan sentinel) => sentinel.Kind is LiteralKind.Boolean
        ? sentinel.Values.Select(static value => (ExpressionSyntax)SyntaxFactory.LiteralExpression(
            StringComparer.Ordinal.Equals(value, "true") ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression))
        : sentinel.Values.Select(static value => (ExpressionSyntax)SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value)));

    private static MethodDeclarationSyntax EmitReadKnown(bool fromElement)
    {
        var getTypeInfo = GetTypeInfo();
        var deserialize = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonSerializer"), "Deserialize"),
            fromElement ? SyntaxFactory.Argument(SyntaxFactory.IdentifierName("element")) : ReaderArgument(),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("typeInfo")));
        var parameter = fromElement
            ? SyntaxFactory.Parameter(SyntaxFactory.Identifier("element")).WithType(SyntaxFactory.IdentifierName("JsonElement"))
            : SyntaxFactory
                .Parameter(SyntaxFactory.Identifier("reader"))
                .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader"))
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)));
        return SyntaxFactory
            .MethodDeclaration(SyntaxFactory.IdentifierName("T"), "ReadKnown")
            .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter("T"))))
            .WithConstraintClauses(SyntaxFactory.SingletonList(NotNullConstraint()))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(parameter)))
            .WithBody(SyntaxFactory.Block(
                Local("typeInfo", getTypeInfo),
                Local("result", deserialize),
                SyntaxFactory.IfStatement(
                    SyntaxFactory.IsPatternExpression(
                        SyntaxFactory.IdentifierName("result"),
                        SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                    SyntaxFactory.ThrowStatement(JsonException("The structural union arm materialized null."))),
                SyntaxFactory.ReturnStatement(SyntaxFactory.CastExpression(
                    SyntaxFactory.IdentifierName("T"),
                    SyntaxFactory.IdentifierName("result")))));
    }

    private static MethodDeclarationSyntax EmitWriteKnown()
    {
        var serialize = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonSerializer"), "Serialize"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("writer")),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("value")),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("typeInfo")));
        return SyntaxFactory
            .MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "WriteKnown")
            .WithTypeParameterList(SyntaxFactory.TypeParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.TypeParameter("T"))))
            .WithConstraintClauses(SyntaxFactory.SingletonList(NotNullConstraint()))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
            [
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("writer")).WithType(SyntaxFactory.IdentifierName("Utf8JsonWriter")),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(SyntaxFactory.IdentifierName("T")),
            ])))
            .WithBody(SyntaxFactory.Block(
                Local("typeInfo", GetTypeInfo()),
                SyntaxFactory.ExpressionStatement(serialize)));
    }

    private static BinaryExpressionSyntax GetTypeInfo()
    {
        var get = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(EmissionSyntax.MemberAccess(
                SyntaxFactory.IdentifierName("OpenCodeJsonContext"), "Default"), "GetTypeInfo"),
            SyntaxFactory.Argument(SyntaxFactory.TypeOfExpression(SyntaxFactory.IdentifierName("T"))));
        return SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            get,
            SyntaxFactory.ThrowExpression(JsonException("The generated context has no structural union arm metadata.")));
    }

    private static MethodDeclarationSyntax EmitReadUnknown(StructuralUnionModelPlan union)
    {
        var parse = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonDocument"), "ParseValue"),
            SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader")).WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)));
        var document = SyntaxFactory
            .LocalDeclarationStatement(SyntaxFactory
                .VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator("document").WithInitializer(SyntaxFactory.EqualsValueClause(parse)))))
            .WithUsingKeyword(SyntaxFactory.Token(SyntaxKind.UsingKeyword));
        var root = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("document"), "RootElement");
        return SyntaxFactory
            .MethodDeclaration(TypeSyntaxEmitter.EmitNamed(union.Name), "ReadUnknown")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory
                    .Parameter(SyntaxFactory.Identifier("reader"))
                    .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader"))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))))))
            .WithBody(SyntaxFactory.Block(
                document,
                SyntaxFactory.ReturnStatement(EmissionSyntax.Invocation(
                    EmissionSyntax.MemberAccess(TypeSyntaxEmitter.EmitNamed(union.Name), "FromUnknown"),
                    SyntaxFactory.Argument(root)))));
    }

    private static ParameterListSyntax ConverterReadParameters() => SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
    [
        SyntaxFactory
            .Parameter(SyntaxFactory.Identifier("reader"))
            .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader"))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))),
        SyntaxFactory.Parameter(SyntaxFactory.Identifier("typeToConvert")).WithType(SyntaxFactory.IdentifierName("Type")),
        SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(SyntaxFactory.IdentifierName("JsonSerializerOptions")),
    ]));

    private static TypeParameterConstraintClauseSyntax NotNullConstraint() =>
        SyntaxFactory
            .TypeParameterConstraintClause("T")
            .WithConstraints(SyntaxFactory.SingletonSeparatedList<TypeParameterConstraintSyntax>(
                SyntaxFactory.TypeConstraint(SyntaxFactory.IdentifierName("notnull"))));

    private static ParameterListSyntax ConverterWriteParameters(StructuralUnionModelPlan union) =>
        SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
        [
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("writer")).WithType(SyntaxFactory.IdentifierName("Utf8JsonWriter")),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(TypeSyntaxEmitter.EmitNamed(union.Name)),
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("options")).WithType(SyntaxFactory.IdentifierName("JsonSerializerOptions")),
        ]));

    private static LocalDeclarationStatementSyntax Local(string name, ExpressionSyntax value) =>
        SyntaxFactory.LocalDeclarationStatement(SyntaxFactory
            .VariableDeclaration(SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(name).WithInitializer(SyntaxFactory.EqualsValueClause(value)))));

    private static ObjectCreationExpressionSyntax JsonException(string message) => SyntaxFactory
        .ObjectCreationExpression(
            SyntaxFactory.IdentifierName("JsonException"))
        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(message))))));

    private static string DisplayName(string name) =>
        string.Join(' ', CSharpNamePolicy.SplitWords(name).Select(static word => word.ToLowerInvariant()));
}
