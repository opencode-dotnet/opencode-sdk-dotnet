using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class StructuralUnionConverterEmitter
{
    internal static GeneratedSource EmitConverter(StructuralUnionModelPlan union, string relativePath, GeneratedNamespacePlan namespaces)
    {
        var declaration = ConverterSyntax.Declaration(ConverterNamePolicy.Of(union.Name),
            TypeSyntaxEmitter.EmitNamed(union.Name), ConverterMembers(union), default);
        var unit = EmissionSyntax.CompilationUnit(
            namespaces.Internal,
            [
                namespaces.Models,
                "System",
                "System.Text.Json",
                "System.Text.Json.Serialization",
            ],
            [declaration]);
        return EmissionSyntax.CreateSource(relativePath, unit);
    }

    private static IEnumerable<MemberDeclarationSyntax> ConverterMembers(StructuralUnionModelPlan union)
    {
        var firstMatch = union.Arms.Any(static arm => arm.Claim is not null);
        yield return EmitRead(union);
        yield return EmitWrite(union);
        if (firstMatch)
        {
            yield return StructuralUnionClaimEmitter.EmitReadObject(union);
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
                        : SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException(
                            $"JSON token '{token}' cannot start this structural union value.")))))
            .Append(SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.DiscardPattern(),
                SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException("The JSON token kind is not recognized."))))
            .ToArray();
        var tokenType = EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType");
        var body = new List<StatementSyntax>
        {
            SyntaxFactory.ReturnStatement(SyntaxFactory
                .SwitchExpression(tokenType)
                .WithArms(SyntaxFactory.SeparatedList(arms))),
        };
        return ConverterSyntax.Read(TypeSyntaxEmitter.EmitNamed(union.Name), SyntaxFactory.Block(body), ["typeToConvert", "options"]);
    }

    private static bool IsValueStartToken(JsonTokenType token) => token is
        JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.String
        or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False;

    private static ExpressionSyntax EmitReadValue(TypeReferencePlan type) => type switch
    {
        NamedTypeReferencePlan { Name: "string" } => SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetString")),
            SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException("The string arm materialized null."))),
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
                .WithLabels(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(StructuralUnionCarrierEmitter.KindMember(union, arm.Name))))
                .WithStatements(SyntaxFactory.List<StatementSyntax>(
                [
                    SyntaxFactory.ExpressionStatement(EmitWriteValue(arm)),
                    SyntaxFactory.BreakStatement(),
                ])))
            .Append(SyntaxFactory
                .SwitchSection()
                .WithLabels(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(StructuralUnionCarrierEmitter.KindMember(union, "Unknown"))))
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
                    EmissionSyntax.JsonException("The structural union kind is not recognized.")))))
            .ToArray();
        var body = new List<StatementSyntax>
        {
            SyntaxFactory
                .SwitchStatement(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("value"), "Kind"))
                .WithSections(SyntaxFactory.List(sections)),
        };
        return ConverterSyntax.Write(TypeSyntaxEmitter.EmitNamed(union.Name), SyntaxFactory.Block(body), ["writer", "value", "options"]);
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
                EmissionSyntax.Local("typeInfo", getTypeInfo),
                EmissionSyntax.Local("result", deserialize),
                SyntaxFactory.IfStatement(
                    SyntaxFactory.IsPatternExpression(
                        SyntaxFactory.IdentifierName("result"),
                        SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                    SyntaxFactory.ThrowStatement(EmissionSyntax.JsonException("The structural union arm materialized null."))),
                SyntaxFactory.ReturnStatement(SyntaxFactory.CastExpression(
                    SyntaxFactory.IdentifierName("T"),
                    SyntaxFactory.IdentifierName("result")))));
    }

    private static MethodDeclarationSyntax EmitWriteKnown()
    {
        var serialize = ConverterSyntax.SerializeWithTypeInfo(
            SyntaxFactory.IdentifierName("value"), SyntaxFactory.IdentifierName("typeInfo"));
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
                EmissionSyntax.Local("typeInfo", GetTypeInfo()),
                SyntaxFactory.ExpressionStatement(serialize)));
    }

    private static BinaryExpressionSyntax GetTypeInfo()
    {
        var get = EmissionSyntax.Invocation(
            EmissionSyntax.MemberAccess(RegistryNames.Default, "GetTypeInfo"),
            SyntaxFactory.Argument(SyntaxFactory.TypeOfExpression(SyntaxFactory.IdentifierName("T"))));
        return SyntaxFactory.BinaryExpression(
            SyntaxKind.CoalesceExpression,
            get,
            SyntaxFactory.ThrowExpression(EmissionSyntax.JsonException("The generated context has no structural union arm metadata.")));
    }

    private static MethodDeclarationSyntax EmitReadUnknown(StructuralUnionModelPlan union)
    {
        var document = ConverterSyntax.PayloadDocument();
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

    private static TypeParameterConstraintClauseSyntax NotNullConstraint() =>
        SyntaxFactory
            .TypeParameterConstraintClause("T")
            .WithConstraints(SyntaxFactory.SingletonSeparatedList<TypeParameterConstraintSyntax>(
                SyntaxFactory.TypeConstraint(SyntaxFactory.IdentifierName("notnull"))));
}
