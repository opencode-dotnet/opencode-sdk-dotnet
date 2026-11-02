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
        var statements = new List<StatementSyntax> { Require(TokenIs("StartObject")) };
        statements.AddRange(properties.Select((property, index) => Local(plan, property, index)));
        var required = properties.Where(static property => property.IsRequired).ToArray();
        for (var word = 0; word < (required.Length + 63) / 64; word++)
        {
            statements.Add(Declare(Mask(word), SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ULongKeyword)), Number(0)));
        }

        var cases = properties.Select((property, index) => MemberCase(property, index, Array.IndexOf(required, property)))
            .Append(SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.DefaultSwitchLabel()),
                SyntaxFactory.List<StatementSyntax>([Require(Call("TrySkip")), SyntaxFactory.BreakStatement()])));
        var loop = SyntaxFactory.Block(
            SyntaxFactory.IfStatement(TokenIs("EndObject"), SyntaxFactory.Block(SyntaxFactory.List(
                [.. PresenceChecks(required.Length), Construct(plan, record, properties)]))),
            Require(TokenIs("PropertyName")), MemberNumber(properties), Require(Call("Read")),
            SyntaxFactory.SwitchStatement(SyntaxFactory.IdentifierName("member")).WithSections(SyntaxFactory.List(cases)));
        statements.Add(SyntaxFactory.WhileStatement(Call("Read"), loop));
        statements.Add(ConverterSyntax.ThrowJson("The record ended before its closing object token."));
        var method = ConverterSyntax.Read(SyntaxFactory.IdentifierName(record.Name), SyntaxFactory.Block(statements), [])
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        // A record-specific delegate needs no runtime type-to-convert parameter.
        method = method.WithParameterList(method.ParameterList.WithParameters(method.ParameterList.Parameters.RemoveAt(1)));
        var name = ConverterNamePolicy.Reader(record.Name);
        var declaration = SyntaxFactory.ClassDeclaration(name)
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InternalKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>([.. RecordMemberReaderEmitter.Fields(properties), method]));
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

    private static LocalDeclarationStatementSyntax Local(EmitPlan plan, ModelPropertyPlan property, int index)
    {
        var type = RecordMemberReaderEmitter.PropertyType(property);
        // Reference locals can remain null until the presence mask and immediate null checks prove construction safe.
        if (!property.EmitsOptionalWrapper && !property.Type.IsNullable && IsReference(plan, property.Type))
        {
            type = SyntaxFactory.NullableType(type);
        }
        return Declare(Value(index), type, SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression));
    }

    private static ReturnStatementSyntax Construct(EmitPlan plan, ObjectModelPlan record, IReadOnlyList<ModelPropertyPlan> properties)
    {
        var assignments = properties.Select((property, index) =>
        {
            ExpressionSyntax value = SyntaxFactory.IdentifierName(Value(index));
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

    private static LocalDeclarationStatementSyntax MemberNumber(ModelPropertyPlan[] properties)
    {
        ExpressionSyntax selection = Number(-1);
        for (var index = properties.Length - 1; index >= 0; index--)
        {
            var utf8 = SyntaxFactory.LiteralExpression(SyntaxKind.Utf8StringLiteralExpression,
                SyntaxFactory.Token(default, SyntaxKind.Utf8StringLiteralToken,
                    SymbolDisplay.FormatLiteral(properties[index].WireName, quote: true) + "u8", properties[index].WireName, default));
            selection = SyntaxFactory.ConditionalExpression(Call("ValueTextEquals", SyntaxFactory.Argument(utf8)), Number(index), selection);
        }
        return EmissionSyntax.Local("member", selection);
    }

    private static SwitchSectionSyntax MemberCase(ModelPropertyPlan property, int index, int requiredIndex)
    {
        var statements = RecordMemberReaderEmitter.Assignment(property, Value(index)).ToList();
        if (requiredIndex >= 0)
        {
            statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.OrAssignmentExpression,
                SyntaxFactory.IdentifierName(Mask(requiredIndex / 64)), Unsigned(1UL << (requiredIndex % 64)))));
        }
        statements.Add(SyntaxFactory.BreakStatement());
        return SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(Number(index))),
            SyntaxFactory.List(statements));
    }

    private static IEnumerable<StatementSyntax> PresenceChecks(int count)
    {
        for (var word = 0; word < (count + 63) / 64; word++)
        {
            var bits = Math.Min(64, count - (word * 64));
            yield return Require(SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, SyntaxFactory.IdentifierName(Mask(word)),
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

    private static LiteralExpressionSyntax Number(int value) => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
    private static LocalDeclarationStatementSyntax Declare(string name, TypeSyntax type, ExpressionSyntax value) =>
        SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(type,
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(name).WithInitializer(SyntaxFactory.EqualsValueClause(value)))));
    private static LiteralExpressionSyntax Unsigned(ulong value) => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value));
    private static string Value(int index) => "value" + index.ToString(CultureInfo.InvariantCulture);
    private static string Mask(int word) => "present" + word.ToString(CultureInfo.InvariantCulture);
}
