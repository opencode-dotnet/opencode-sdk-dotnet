using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class GeneratedDocumentationTests
{
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Summaries_Should_Keep_Shrink_Only_Placeholder_Ceilings_And_Reject_Grammar_Defects()
    {
        var summaries = await LoadSummariesAsync();
        await Assert.That(summaries).IsNotEmpty();
        string[] placeholders =
        [
            @"^Gets the (?:[a-z0-9 ]+ value|value)\.$",
            @"^Gets the (?:[a-z0-9 ]+ value|value)\. Every declared variant carries it",
            @"^Represents an? [a-z0-9 ]+ value\.$",
            @"^Represents an? [a-z0-9 ]+ structural union\.$",
            @"^Represents an? [a-z0-9 ]+ union\.$",
            @"^Creates an? [a-z0-9 ]+ from its [a-z0-9 ]+ arm\.$",
            @"^Represents the [a-z0-9 ]+ arm\.$",
            @"^Represents the shared .* shape of every .* variant\.$",
            @"^The '\w+' collection client\.$",
            @"^Gets the '\w+' collection client\.$",
            @"^(Gets the '[^']*' wire value|The '[^']*' wire spelling)\.$",
        ];
        var historical = summaries.Values.Count(static text => Matches(text, @"^Gets the [a-z ]+ value\.$", ignoreCase: false));
        var normalized = summaries.Values.Count(static text => Matches(text, @"^Gets the [a-z0-9 ]+ value\.$"));
        var categories = summaries.Values.Select(text => Array.FindIndex(placeholders, pattern => Matches(text, pattern))).ToArray();
        var broad = categories.Count(static category => category >= 0);
        var output = TestContext.Current!.Output;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Public: {summaries.Count}; historical: {historical} ({100m * historical / summaries.Count}%); normalized: {normalized}; broad: {broad} ({100m * broad / summaries.Count}%)."));
        foreach (var category in categories.Where(static category => category >= 0).GroupBy(static category => category))
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{placeholders[category.Key]}: {category.Count()}"));
        }

        string[] defects =
        [
            @"\ba (?!(?:user|users|union|unions|unit|units|unicode|unique|universal|university|one)\b)[aeiou]|\ba (?:heir|heirs|honest|honor|honors|hour|hours|MCP)\b|\ban (?:user|users|union|unions|unit|units|unicode|unique|universal|university|one)\b",
            @"\b(?:o auth|open code)\b",
            @"\b(?:v|p|base|OAuth|OpenCode|MCP) \d+\b",
            @"\bvalue value\b",
            @"\b(?!value\b)(\w+)\s+\1\b",
        ];
        var helperFed = summaries.Where(pair => placeholders.Any(pattern => Matches(pair.Value, pattern)) ||
            Matches(pair.Value, @"^(?:Preserves an unknown [a-z0-9 ]+ payload\.$|The [a-z0-9 ]+\. The pinned server's values are the static properties;)")).ToArray();
        using var assertions = Assert.Multiple();
        await Assert.That(100m * historical / summaries.Count).IsLessThanOrEqualTo(44.702440m);
        await Assert.That(100m * broad / summaries.Count).IsLessThanOrEqualTo(61.073171m);
        foreach (var defect in defects)
        {
            var offenders = helperFed.Where(pair => Matches(pair.Value, defect)).ToArray();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Grammar defects: {offenders.Length}; {string.Join("; ", offenders.Select(static pair => $"{pair.Key}: {pair.Value}"))}"));
            await Assert.That(offenders).IsEmpty();
        }
    }

    private static bool Matches(string text, string pattern, bool ignoreCase = true) =>
        Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), Regex.InfiniteMatchTimeout);

    private static async Task<IReadOnlyDictionary<string, string>> LoadSummariesAsync()
    {
        var fileSystem = new RealFileSystem();
        var root = fileSystem.Path.Combine(new RepositoryRoot(fileSystem).Locate(), "src", "OpenCode.Sdk");
        using var manifest = JsonDocument.Parse(await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, ".generated-manifest.json"), CancellationToken.None));
        var paths = manifest.RootElement.GetProperty("files").EnumerateArray().Select(static path => path.GetString()!).ToArray();
        await Assert.That(paths).IsNotEmpty();
        var trees = await Task.WhenAll(paths.Select(async path => CSharpSyntaxTree.ParseText(
            await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, path), CancellationToken.None), path: path)));

        var compilation = CSharpCompilation.Create("GeneratedDocumentation", trees);
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in (await tree.GetRootAsync()).DescendantNodes()
                .Where(static node => node is MemberDeclarationSyntax or VariableDeclaratorSyntax))
            {
                var symbol = model.GetDeclaredSymbol(declaration);
                if (symbol is null or INamespaceSymbol || !IsPublic(symbol))
                {
                    continue;
                }

                var key = string.Create(CultureInfo.InvariantCulture, $"{tree.FilePath}:{declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {symbol}");
                var xml = symbol.GetDocumentationCommentXml();
                var summary = string.IsNullOrWhiteSpace(xml) ? null : XElement.Parse(xml).Element("summary");
                foreach (var see in summary?.Descendants("see").ToArray() ?? [])
                {
                    var cref = (string?)see.Attribute("cref");
                    see.ReplaceWith((string?)see.Attribute("langword") ?? (cref is null ? see.Value : cref[(cref.LastIndexOfAny(['.', ':']) + 1)..]));
                }

                var text = string.Join(' ', (summary?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (text.Length == 0)
                {
                    missing.Add(key);
                }

                summaries.Add(key, text);
            }
        }

        await Assert.That(missing).IsEmpty();
        return summaries;
    }

    private static bool IsPublic(ISymbol symbol)
    {
        for (var current = symbol; current is not (null or INamespaceSymbol); current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }
}
