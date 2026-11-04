using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>Emits a record shell with per-occurrence null checks and fixed-word required presence.</summary>
internal static class RecordReaderEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(EmitPlan plan, IReadOnlyDictionary<string, ObjectModelPlan> readers)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readers);
        return [.. readers.Values.OrderBy(static record => record.Name, StringComparer.Ordinal).Select(record => EmitRecord(plan, record))];
    }

    private static GeneratedSource EmitRecord(EmitPlan plan, ObjectModelPlan record)
    {
        var properties = record.Properties.Where(property => !record.ChainMarkers.Any(marker =>
            marker.WireName == property.WireName && marker.Prefix is null && property.IsLiteral)).ToArray();
        var statements = new List<StatementSyntax> { Require(TokenIs("StartObject")),
            Declare(RecordMemberReaderEmitter.StateName, SyntaxFactory.IdentifierName(RecordMemberReaderEmitter.StateTypeName), SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)) };
        var required = properties.Where(static property => property.IsRequired).ToArray();
        var groups = properties.Chunk(12).DefaultIfEmpty([]).ToArray();
        var matches = Enumerable.Range(0, groups.Length).Select(index => (ExpressionSyntax)GroupCall("TryReadGroup" + index.ToString(CultureInfo.InvariantCulture)))
            .Aggregate(static (left, right) => SyntaxFactory.BinaryExpression(SyntaxKind.LogicalOrExpression, left, right));
        var loop = SyntaxFactory.Block(
            SyntaxFactory.IfStatement(TokenIs("EndObject"), SyntaxFactory.Block(SyntaxFactory.List(
                [.. PresenceChecks(required.Length), SyntaxFactory.ReturnStatement(EmissionSyntax.Invocation(SyntaxFactory.IdentifierName("Construct"), RecordMemberReaderEmitter.StateArgument()))]))),
            Require(TokenIs("PropertyName")), SyntaxFactory.IfStatement(matches, SyntaxFactory.Block(SyntaxFactory.ContinueStatement())),
            SyntaxFactory.ExpressionStatement(EmissionSyntax.Invocation(SyntaxFactory.IdentifierName(RecordMemberReaderEmitter.AdvanceToValueName),
                RecordMemberReaderEmitter.ReaderArgument())), Require(Call("TrySkip")));
        statements.Add(SyntaxFactory.WhileStatement(Call("Read"), loop));
        statements.Add(ConverterSyntax.ThrowJson("The record ended before its closing object token."));
        var method = ConverterSyntax.Read(SyntaxFactory.IdentifierName(record.Name), SyntaxFactory.Block(statements), [])
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        // A record-specific delegate needs no runtime type-to-convert parameter.
        method = method.WithParameterList(method.ParameterList.WithParameters(method.ParameterList.Parameters.RemoveAt(1)));
        if (!method.DescendantNodes().OfType<IdentifierNameSyntax>().Any(static name => name.Identifier.ValueText == "options"))
        {
            var options = method.ParameterList.Parameters[1];
            method = method.ReplaceNode(options, options.WithIdentifier(SyntaxFactory.Identifier("_")));
        }
        var name = ConverterNamePolicy.Reader(record.Name);
        var declaration = SyntaxFactory.ClassDeclaration(name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>([.. RecordMemberReaderEmitter.Fields(properties), State(plan, properties, required.Length), method,
                .. groups.Select(Group), AdvanceToValue(),
                SyntaxFactory.MethodDeclaration(SyntaxFactory.IdentifierName(record.Name), "Construct")
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
                    .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(RecordMemberReaderEmitter.StateParameter())))
                    .WithBody(SyntaxFactory.Block(Construct(plan, record, properties))),
                .. properties.Select((property, index) => RecordMemberReaderEmitter.Reader(property, Value(index),
                    PresenceSet(Array.IndexOf(required, property)), plan.ReaderCallSites.GetValueOrDefault(record.Name)))]));
        var usings = new HashSet<string>(StringComparer.Ordinal) { plan.Namespaces.Models, "System", "System.Text.Json" };
        foreach (var property in properties)
        {
            TypeUsingCollector.Collect(property.Type, usings);
        }
        var source = EmissionSyntax.CreateSource(GeneratedPlacement.ConverterPathOf(plan.Placement, record.Name, name),
            EmissionSyntax.CompilationUnit(plan.Namespaces.Internal, [.. usings.Order(StringComparer.Ordinal)], [declaration]));
        // Roslyn's normalizer inserts a space between a nullable type and default's closing parenthesis.
        var syntax = SyntaxFactory.ParseCompilationUnit(Encoding.UTF8.GetString(source.Utf8Source.Span));
        var nullableDefaults = syntax.DescendantNodes().OfType<DefaultExpressionSyntax>()
            .Where(static expression => expression.Type is NullableTypeSyntax).Select(static expression => expression.Type.GetLastToken());
        return source with
        {
            Utf8Source = Encoding.UTF8.GetBytes(syntax.ReplaceTokens(nullableDefaults,
                static (token, _) => token.WithTrailingTrivia()).ToFullString())
        };
    }

    private static VariableDeclarationSyntax Field(EmitPlan plan, ModelPropertyPlan property, int index)
    {
        var type = RecordMemberReaderEmitter.PropertyType(property);
        // Reference fields can remain null until the presence mask and immediate null checks prove construction safe.
        if (!property.EmitsOptionalWrapper && !property.Type.IsNullable && IsReference(plan, property.Type))
        {
            type = SyntaxFactory.NullableType(type);
        }
        return SyntaxFactory.VariableDeclaration(type, SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(Value(index))));
    }

    private static StructDeclarationSyntax State(EmitPlan plan, ModelPropertyPlan[] properties, int requiredCount)
    {
        var declarations = properties.Select((property, index) => Field(plan, property, index))
            .Concat(Enumerable.Range(0, (requiredCount + 63) / 64).Select(word =>
                SyntaxFactory.VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ULongKeyword)),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(Mask(word))))));
        return SyntaxFactory.StructDeclaration(RecordMemberReaderEmitter.StateTypeName).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(declarations.Select(declaration =>
                SyntaxFactory.FieldDeclaration(declaration)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))))));
    }

    private static ReturnStatementSyntax Construct(EmitPlan plan, ObjectModelPlan record, IReadOnlyList<ModelPropertyPlan> properties)
    {
        var assignments = properties.Select((property, index) =>
        {
            ExpressionSyntax value = RecordMemberReaderEmitter.StateMember(Value(index));
            if (!property.EmitsOptionalWrapper && !property.Type.IsNullable && IsReference(plan, property.Type))
            {
                value = SyntaxFactory.PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, value);
            }
            return (ExpressionSyntax)SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(property.Name), value);
        });
        return SyntaxFactory.ReturnStatement(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(record.Name))
                .WithInitializer(SyntaxFactory.InitializerExpression(SyntaxKind.ObjectInitializerExpression, SyntaxFactory.SeparatedList(assignments))))
            .WithLeadingTrivia(SyntaxFactory.Comment("// The presence mask and immediate null checks prove required reference members are assigned."), SyntaxFactory.ElasticCarriageReturnLineFeed);
    }

    private static MethodDeclarationSyntax Group(ModelPropertyPlan[] properties, int index)
    {
        var statements = new List<StatementSyntax>();
        foreach (var property in properties)
        {
            var utf8 = SyntaxFactory.LiteralExpression(SyntaxKind.Utf8StringLiteralExpression,
                SyntaxFactory.Token(default, SyntaxKind.Utf8StringLiteralToken,
                    SymbolDisplay.FormatLiteral(property.WireName, quote: true) + "u8", property.WireName, default));
            statements.Add(SyntaxFactory.IfStatement(Call("ValueTextEquals", SyntaxFactory.Argument(utf8)),
                SyntaxFactory.Block(SyntaxFactory.ReturnStatement(GroupCall("Read" + property.Name)))));
        }
        statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));
        var method = ConverterSyntax.Read(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), SyntaxFactory.Block(statements), [])
            .WithIdentifier(SyntaxFactory.Identifier("TryReadGroup" + index.ToString(CultureInfo.InvariantCulture)))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        return method.WithParameterList(method.ParameterList.WithParameters(method.ParameterList.Parameters.Replace(method.ParameterList.Parameters[1], RecordMemberReaderEmitter.StateParameter())));
    }

    private static MethodDeclarationSyntax AdvanceToValue() => SyntaxFactory.MethodDeclaration(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), RecordMemberReaderEmitter.AdvanceToValueName)
        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
        .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Parameter(SyntaxFactory.Identifier("reader"))
            .WithType(SyntaxFactory.IdentifierName("Utf8JsonReader")).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))))))
        .WithBody(SyntaxFactory.Block(Require(Call("Read"))));

    private static ExpressionStatementSyntax? PresenceSet(int index) => index < 0 ? null : SyntaxFactory.ExpressionStatement(
        SyntaxFactory.AssignmentExpression(SyntaxKind.OrAssignmentExpression, RecordMemberReaderEmitter.StateMember(Mask(index / 64)), Unsigned(1UL << (index % 64))));

    private static InvocationExpressionSyntax GroupCall(string name) => EmissionSyntax.Invocation(SyntaxFactory.IdentifierName(name),
        RecordMemberReaderEmitter.ReaderArgument(),
        RecordMemberReaderEmitter.StateArgument(), SyntaxFactory.Argument(SyntaxFactory.IdentifierName("options")));

    private static IEnumerable<StatementSyntax> PresenceChecks(int count)
    {
        for (var word = 0; word < (count + 63) / 64; word++)
        {
            var bits = Math.Min(64, count - (word * 64));
            yield return Require(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, RecordMemberReaderEmitter.StateMember(Mask(word)),
                Unsigned(bits == 64 ? ulong.MaxValue : (1UL << bits) - 1)));
        }
    }

    private static bool IsReference(EmitPlan plan, TypeReferencePlan type) => type switch
    {
        ListTypeReferencePlan or DictionaryTypeReferencePlan => true,
        NamedTypeReferencePlan { Name: "string" or "Uri" } => true,
        NamedTypeReferencePlan named => plan.Unions.Any(union => union.Name == named.Name)
            || plan.Models.Any(model => model.Name == named.Name && model is not EnumModelPlan),
        _ => false,
    };

    private static IfStatementSyntax Require(ExpressionSyntax condition) => SyntaxFactory.IfStatement(
        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(condition)),
        SyntaxFactory.Block(ConverterSyntax.ThrowJson("The record cannot be materialized.")));

    private static BinaryExpressionSyntax TokenIs(string token) => SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression,
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), "TokenType"),
        EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("JsonTokenType"), token));

    private static InvocationExpressionSyntax Call(string method, params ArgumentSyntax[] arguments) =>
        EmissionSyntax.Invocation(EmissionSyntax.MemberAccess(SyntaxFactory.IdentifierName("reader"), method), arguments);

    private static LocalDeclarationStatementSyntax Declare(string name, TypeSyntax type, ExpressionSyntax value) =>
        SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(type,
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(name).WithInitializer(SyntaxFactory.EqualsValueClause(value)))));
    private static LiteralExpressionSyntax Unsigned(ulong value) => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
    private static string Value(int index) => "value" + index.ToString(CultureInfo.InvariantCulture);
    private static string Mask(int word) => "present" + word.ToString(CultureInfo.InvariantCulture);
}
