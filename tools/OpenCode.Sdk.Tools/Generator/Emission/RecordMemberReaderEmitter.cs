using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>Emits the property's conversion, including the null handling a direct converter bypasses.</summary>
internal static class RecordMemberReaderEmitter
{
    internal const string StateName = "state";
    internal const string StateTypeName = "State";
    internal const string AdvanceToValueName = "AdvanceToValue";

    internal static MemberAccessExpressionSyntax StateMember(string name) => EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName(StateName), name);
    internal static ArgumentSyntax StateArgument() => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(StateName))
        .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword));
    internal static ParameterSyntax StateParameter() => SyntaxFactory.Parameter(SyntaxFactory.Identifier(StateName))
        .WithType(SyntaxFactory.IdentifierName(StateTypeName)).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)));
    internal static ArgumentSyntax ReaderArgument() => SyntaxFactory.Argument(SyntaxFactory.IdentifierName("reader"))
        .WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword));

    internal static MethodDeclarationSyntax Reader(ModelPropertyPlan property, string field, StatementSyntax? presence, IReadOnlySet<string>? callSites)
    {
        var statements = new List<StatementSyntax> { SyntaxFactory.ExpressionStatement(
            EmissionSyntax.Invocation(SyntaxFactory.IdentifierName(AdvanceToValueName), ReaderArgument())) };
        statements.AddRange(Assignment(property, field, callSites));
        if (presence is not null)
        {
            statements.Add(presence);
        }
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)));
        var body = SyntaxFactory.Block(statements);
        var options = body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(static name => name.Identifier.ValueText == "options") ? "options" : "_";
        var method = ConverterSyntax.Read(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), body, [])
            .WithIdentifier(SyntaxFactory.Identifier("Read" + property.Name))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        var parameters = method.ParameterList.Parameters;
        parameters = parameters.Replace(parameters[1], StateParameter());
        return method.WithParameterList(method.ParameterList.WithParameters(parameters
            .Replace(parameters[2], parameters[2].WithIdentifier(SyntaxFactory.Identifier(options)))));
    }

    internal static TypeSyntax PropertyType(ModelPropertyPlan property) => property.EmitsOptionalWrapper
        ? TypeSyntaxEmitter.Generic("Optional", TypeSyntaxEmitter.Emit(property.Type)) : TypeSyntaxEmitter.Emit(property.Type);

    internal static IEnumerable<FieldDeclarationSyntax> Fields(IReadOnlyList<ModelPropertyPlan> properties) => properties
        .Where(static property => property.EmitsOptionalWrapper)
        .DistinctBy(static property => ConverterNamePolicy.Optional(property.Type), StringComparer.Ordinal)
        .Select(static property => SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName(ConverterNamePolicy.Optional(property.Type)),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(ConverterNamePolicy.Optional(property.Type))
                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.IdentifierName(ConverterNamePolicy.Optional(property.Type))).WithArgumentList(SyntaxFactory.ArgumentList()))))))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword))));

    internal static IReadOnlyList<StatementSyntax> Assignment(ModelPropertyPlan property, string field, IReadOnlySet<string>? callSites)
    {
        var read = Value(property, callSites);
        var nullToken = SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType"),
            EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), "Null"));
        var statements = new List<StatementSyntax>();
        // Binary converters read null in band even though the bound binary shape retains ClrNull.
        if (!property.EmitsOptionalWrapper && property.Type is not BinaryTypeReferencePlan
            && property.Type.JsonNullRepresentation is JsonNullRepresentation.ClrNull)
        {
            if (property.Type.IsNullable)
            {
                read = SyntaxFactory.ConditionalExpression(nullToken, SyntaxFactory.DefaultExpression(PropertyType(property)), read);
            }
            else
            {
                statements.Add(SyntaxFactory.IfStatement(nullToken, SyntaxFactory.Block(ConverterSyntax.ThrowJson("A non-nullable member was null."))));
            }
        }

        statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
            StateMember(field), read)));
        return statements;
    }

    private static ExpressionSyntax Value(ModelPropertyPlan property, IReadOnlySet<string>? callSites)
    {
        if (callSites?.Contains(property.Name) == true && property.Type is NamedTypeReferencePlan nested)
        {
            return EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName(ConverterNamePolicy.Reader(nested.Name)), "Read"),
                ReaderArgument(), SyntaxFactory.Argument(SyntaxFactory.IdentifierName("options")));
        }

        if (property.EmitsOptionalWrapper)
        {
            return EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(
                    SyntaxFactory.IdentifierName(ConverterNamePolicy.Optional(property.Type)), "Read"),
                ReaderArgument(), SyntaxFactory.Argument(SyntaxFactory.TypeOfExpression(PropertyType(property))),
                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("options")));
        }

        if (property.Type is SpecialNumberTypeReferencePlan)
        {
            return SpecialNumber();
        }

        var method = property.Type is NamedTypeReferencePlan named ? named.Name switch
        {
            "string" => "GetString",
            "bool" => "GetBoolean",
            "long" => "GetInt64",
            "double" => "GetDouble",
            _ => null,
        } : null;
        if (method is not null)
        {
            return EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), method));
        }

        return EmissionSyntax.Invocation(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("UnionMemberReader"), TypeSyntaxEmitter.Generic(
                    ContainsSpecialNumber(property.Type) ? "ReadSpecialNumbers" : "Read", TypeSyntaxEmitter.Emit(property.Type))),
            ReaderArgument(), SyntaxFactory.Argument(SyntaxFactory.IdentifierName("options")));
    }

    private static ConditionalExpressionSyntax SpecialNumber()
    {
        var literals = new[] { (Wire: "NaN", Member: "NaN"), (Wire: "Infinity", Member: "PositiveInfinity"), (Wire: "-Infinity", Member: "NegativeInfinity") };
        var arms = literals.Select(static literal => SyntaxFactory.SwitchExpressionArm(
                SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(literal.Wire))),
                EmissionSyntax.MemberAccess(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.DoubleKeyword)), literal.Member)))
            .Append(SyntaxFactory.SwitchExpressionArm(SyntaxFactory.DiscardPattern(), SyntaxFactory.ThrowExpression(
                EmissionSyntax.JsonException("The JSON string is not a named floating-point literal."))));
        var named = SyntaxFactory.SwitchExpression(EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(
            SyntaxFactory.IdentifierName("reader"), "GetString"))).WithArms(SyntaxFactory.SeparatedList(arms));
        return SyntaxFactory.ConditionalExpression(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType"),
                EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), "String")), named,
            EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "GetDouble")));
    }

    private static bool ContainsSpecialNumber(TypeReferencePlan type) => type switch
    {
        SpecialNumberTypeReferencePlan => true,
        ListTypeReferencePlan list => ContainsSpecialNumber(list.ElementType),
        DictionaryTypeReferencePlan dictionary => ContainsSpecialNumber(dictionary.ValueType),
        _ => false,
    };
}
