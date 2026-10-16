using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>
/// A Stopwatch reference outside a using directive; new CancellationTokenSource with non-empty arguments;
/// CancelAfter invocations; Task.Delay invocations.
/// </summary>
internal static class RealClockGuard
{
    public const string Rule = "Real clock";
    public const string Text = "A Stopwatch reference outside a using directive; new CancellationTokenSource(<non-empty>); .CancelAfter(...); Task.Delay(...).";

    public static IReadOnlyList<RepositoryGateSite> Find(TestSourceCorpus corpus)
    {
        var sites = new List<RepositoryGateSite>();
        foreach (var file in corpus.Files)
        {
            foreach (var region in file.Regions)
            {
                var nodes = CSharpSourceReader.Nodes(region).ToArray();
                sites.AddRange(nodes.OfType<SimpleNameSyntax>()
                    .Where(static name => name.Identifier.ValueText == "Stopwatch" && !name.Ancestors().OfType<UsingDirectiveSyntax>().Any())
                    .Select(name => RepositoryGateSite.At(file, region, name, Rule, Text)));
                sites.AddRange(nodes.OfType<ObjectCreationExpressionSyntax>()
                    .Where(static creation => creation.ArgumentList?.Arguments.Count > 0 && creation.Type is
                        IdentifierNameSyntax { Identifier.ValueText: "CancellationTokenSource" }
                        or QualifiedNameSyntax { Right.Identifier.ValueText: "CancellationTokenSource" }
                        or AliasQualifiedNameSyntax { Name.Identifier.ValueText: "CancellationTokenSource" })
                    .Select(creation => RepositoryGateSite.At(file, region, creation, Rule, Text)));
                sites.AddRange(nodes.OfType<InvocationExpressionSyntax>()
                    .Where(static call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CancelAfter" }
                        or MemberBindingExpressionSyntax { Name.Identifier.ValueText: "CancelAfter" }
                        or MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "Delay",
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "Task" } or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Task" },
                    })
                    .Select(call => RepositoryGateSite.At(file, region,
                        call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CancelAfter" } member ? member.Name : call.Expression, Rule, Text)));
            }
        }

        return sites;
    }
}
