using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>
/// Assert.That(&lt;arg&gt;) directly followed by .IsTrue() or .IsFalse(), where &lt;arg&gt; contains an invocation
/// of Any, All, SequenceEqual, SetEquals, ReferenceEquals, DeepEquals, TrueForAll, Exists, Contains or ContainsKey.
/// </summary>
internal static class CollapsedAssertionGuard
{
    public const string Rule = "Rule A";
    public const string Text = "Assert.That(<arg>) directly followed by .IsTrue() or .IsFalse(), where <arg> contains an invocation of Any, All, SequenceEqual, SetEquals, ReferenceEquals, DeepEquals, TrueForAll, Exists, Contains or ContainsKey.";

    public static IReadOnlyList<RepositoryGateSite> Find(TestSourceCorpus corpus)
    {
        var sites = new List<RepositoryGateSite>();
        foreach (var file in corpus.Files)
        {
            foreach (var region in file.Regions)
            {
                var calls = CSharpSourceReader.Nodes(region).OfType<InvocationExpressionSyntax>()
                    .Where(static call => call.Expression is MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" }, Name.Identifier.ValueText: "That",
                    }
                        && call.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "IsTrue" or "IsFalse", Parent: InvocationExpressionSyntax }
                        && call.ArgumentList.DescendantNodes(static node => node is not InterpolatedStringExpressionSyntax)
                            .OfType<InvocationExpressionSyntax>().Any(static invocation => InvokedName(invocation) is
                                "Any" or "All" or "SequenceEqual" or "SetEquals" or "ReferenceEquals" or "DeepEquals" or "TrueForAll" or "Exists" or "Contains" or "ContainsKey"));
                sites.AddRange(calls.Select(call => RepositoryGateSite.At(file, region, call, Rule, Text)));
            }
        }

        return sites;
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };
}
