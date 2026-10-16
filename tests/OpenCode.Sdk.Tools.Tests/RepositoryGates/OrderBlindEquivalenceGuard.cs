using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>An IsEquivalentTo invocation whose arguments do not name CollectionOrdering.</summary>
internal static class OrderBlindEquivalenceGuard
{
    public const string Rule = "Rule B";
    public const string Text = "An IsEquivalentTo invocation whose arguments do not name CollectionOrdering.";

    public static IReadOnlyList<RepositoryGateSite> Find(TestSourceCorpus corpus)
    {
        return [.. from file in corpus.Files
                   from region in file.Regions
                   from call in CSharpSourceReader.Nodes(region).OfType<InvocationExpressionSyntax>()
            where call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "IsEquivalentTo" }
                or MemberBindingExpressionSyntax { Name.Identifier.ValueText: "IsEquivalentTo" }
                       && !call.ArgumentList.DescendantNodes(static node => node is not InterpolatedStringExpressionSyntax)
                           .OfType<SimpleNameSyntax>().Any(static name => name.Identifier.ValueText == "CollectionOrdering")
            select RepositoryGateSite.At(file, region,
                call.Expression is MemberAccessExpressionSyntax member ? member.Name : ((MemberBindingExpressionSyntax)call.Expression).Name, Rule, Text)];
    }
}
