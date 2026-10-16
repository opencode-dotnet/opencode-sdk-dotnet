using Microsoft.CodeAnalysis;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

internal sealed record RepositoryGateSite(string File, int Line, int Span, string Rule, string Detail)
{
    public static RepositoryGateSite At(CSharpSourceFile file, CSharpSourceRegion region, SyntaxNode node, string rule, string detail) =>
        new(file.Path, region.LineOffset + node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, node.SpanStart, rule, detail);

    public override string ToString() => $"{File}:{Line}: {Rule}: {Detail}";
}
