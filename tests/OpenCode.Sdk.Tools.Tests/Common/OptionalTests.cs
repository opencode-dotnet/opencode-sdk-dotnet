using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Common;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class OptionalTests(PinnedBinding pinned)
{
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Value_Should_Report_A_Nullable_Warning_For_A_Non_Nullable_Consumer_Local()
    {
        var consumer = CreateConsumerTree();
        var read = (await consumer.GetRootAsync()).DescendantNodes().OfType<EqualsValueClauseSyntax>().Single().Value;

        var diagnostics = await GeneratedSourceCompiler.CompileConsumerAsync(
            SourceEmitter.Emit(pinned.Plan), consumer, suppressExperimental: false);

        await Assert.That(diagnostics).Contains(diagnostic => diagnostic.Id == "CS8600" &&
            diagnostic.Location.SourceTree == consumer && diagnostic.Location.SourceSpan == read.Span);
    }

    private static SyntaxTree CreateConsumerTree()
    {
        var read = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName("OpenCode.Sdk.Optional<string>")),
            SyntaxFactory.IdentifierName("Value"));
        var local = SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)))
            .AddVariables(SyntaxFactory.VariableDeclarator("value").WithInitializer(SyntaxFactory.EqualsValueClause(read))));
        var method = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), "Probe")
            .WithBody(SyntaxFactory.Block(local));
        var unit = SyntaxFactory.CompilationUnit().AddMembers(SyntaxFactory.ClassDeclaration("Consumer")
            .AddMembers(method));
        return CSharpSyntaxTree.Create(unit, new CSharpParseOptions(LanguageVersion.CSharp14));
    }
}
