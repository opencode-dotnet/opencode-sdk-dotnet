using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class CSharpSourceReader
{
    public static IReadOnlyList<CSharpSourceRegion> Read(SourceText text)
    {
        var regions = new List<CSharpSourceRegion> { new(CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot(), 0) };
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            foreach (var trivia in region.Root.DescendantTrivia().Where(static trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            {
                var offset = region.LineOffset + region.Root.SyntaxTree.GetLineSpan(trivia.Span).StartLinePosition.Line;
                regions.Add(new CSharpSourceRegion(CSharpSyntaxTree.ParseText(trivia.ToString()).GetCompilationUnitRoot(), offset));
            }
        }

        return regions.AsReadOnly();
    }

    public static IEnumerable<SyntaxNode> Nodes(CSharpSourceRegion region) =>
        region.Root.DescendantNodes(static node => node is not LiteralExpressionSyntax and not InterpolatedStringExpressionSyntax);
}
