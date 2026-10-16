using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>A finally clause in a file named *LiveTests.cs.</summary>
internal static class LiveFinallyGuard
{
    public const string Rule = "Live finally";
    public const string Text = "A finally clause in a file named *LiveTests.cs.";

    public static IReadOnlyList<RepositoryGateSite> Find(TestSourceCorpus corpus)
    {
        return [.. from file in corpus.Files
                   where file.Path.EndsWith("LiveTests.cs", StringComparison.Ordinal)
                   from region in file.Regions
                   from clause in CSharpSourceReader.Nodes(region).OfType<FinallyClauseSyntax>()
                   select RepositoryGateSite.At(file, region, clause, Rule, Text)];
    }
}
