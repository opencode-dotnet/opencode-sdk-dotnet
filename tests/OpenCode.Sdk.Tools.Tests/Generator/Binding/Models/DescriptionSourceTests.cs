using System.Globalization;
using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding.Models;

public sealed class DescriptionSourceTests
{
    private const string Declaration = "An explicit upstream declaration.";

    [Test]
    public async Task Sources_Should_Quote_The_Pinned_Upstream_Tree()
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        var (_, _, curation) = await BindingTestHost.LoadPinnedInputsAsync();

        await Assert.That(curation.Descriptions).IsNotEmpty();
        await Assert.That(await CheckAsync(fileSystem, root, curation.Descriptions)).IsEmpty();
    }

    [Test]
    [Arguments("missing", "file does not exist under external/opencode")]
    [Arguments("outside", "excerpt not found within +/-5 lines")]
    [Arguments("absent", "excerpt not found within +/-5 lines")]
    [Arguments("ordinal", "excerpt not found within +/-5 lines")]
    [Arguments("second", "excerpt not found within +/-5 lines")]
    [Arguments("lower", null)]
    [Arguments("upper", null)]
    [Arguments("whitespace", null)]
    [Arguments("substring", null)]
    public async Task Sources_Should_Refuse_Unproven_Excerpts(string fault, string? problem)
    {
        var fileSystem = new MockFileSystem();
        var root = fileSystem.Directory.GetCurrentDirectory();
        var path = fileSystem.Path.Combine(root, "external/opencode/packages/example.ts");
        fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(path)!);
        if (fault != "missing")
        {
            var lines = Enumerable.Repeat("Unrelated declaration.", 12).ToArray();
            var index = fault switch { "lower" => 1, "upper" => 5, _ => 6 };
            lines[index] = fault switch
            {
                "absent" => "Different declaration.",
                "ordinal" => Declaration.ToLowerInvariant(),
                "whitespace" => "An\t explicit   upstream declaration.",
                "substring" => "// " + Declaration,
                _ => Declaration,
            };
            await fileSystem.File.WriteAllLinesAsync(path, lines, CancellationToken.None);
        }

        var location = fault switch { "lower" => "packages/example.ts:7", "whitespace" or "substring" or "ordinal" => "packages/example.ts:2", _ => "packages/example.ts:1" };
        var source = new DescriptionSource { Location = location, Excerpt = Declaration };
        DescriptionCuration[] rows = [new()
        {
            Type = "Notice", Member = "Data", Summary = "The notification data.", Reason = "Verify the evidence oracle.",
            Sources = fault == "second" ? [source with { Location = "packages/example.ts:7" }, source] : [source],
        }];

        await Assert.That(await CheckAsync(fileSystem, root, rows)).IsEquivalentTo(
            problem is null ? [] : new[] { "Notice.Data: " + location + ": " + problem }, CollectionOrdering.Matching);
    }

    private static async Task<IReadOnlyList<string>> CheckAsync(IFileSystem fileSystem, string root, IReadOnlyList<DescriptionCuration> rows)
    {
        var problems = new List<string>();
        foreach (var row in rows)
        {
            var subject = row.Type + (row.Member is null ? "" : "." + row.Member);
            await Assert.That(row.Sources).IsNotNull();
            await Assert.That(row.Sources!).IsNotEmpty();
            // The assertions above require the shape the binder validates before generation.
            foreach (var source in row.Sources!)
            {
                if (source is not { Location: { } location, Excerpt: { } excerpt })
                {
                    throw new InvalidOperationException("The binder must validate source fields before the evidence oracle.");
                }

                var separator = location.LastIndexOf(':', StringComparison.Ordinal);
                var path = fileSystem.Path.Combine(root, "external", "opencode", location[..separator]);
                var prefix = $"{subject}: {location}: ";
                if (!fileSystem.File.Exists(path))
                {
                    problems.Add(prefix + "file does not exist under external/opencode");
                    continue;
                }

                var line = int.Parse(location.AsSpan(separator + 1), CultureInfo.InvariantCulture);
                var lines = await fileSystem.File.ReadAllLinesAsync(path, CancellationToken.None);
                var window = lines.Skip(Math.Max(0, line - 6)).Take(Math.Min(lines.Length, line + 5) - Math.Max(0, line - 6));
                if (!window.Any(candidate => Normalize(candidate).Contains(Normalize(excerpt), StringComparison.Ordinal)))
                {
                    problems.Add(prefix + "excerpt not found within +/-5 lines");
                }
            }
        }

        return problems;
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
