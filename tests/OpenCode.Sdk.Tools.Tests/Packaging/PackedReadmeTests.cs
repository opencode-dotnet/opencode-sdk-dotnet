using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// The README a package carries links the repository at the commit the package was built from,
/// the commit its own nuspec records, so the files it points to match the package. The
/// repository's README links master; only the packed copy is rewritten. CI status badges keep
/// pointing at master, because they report master's state rather than link a file.
/// </summary>
[ClassDataSource<PackedConsumerFeed>(Shared = SharedType.PerTestSession)]
public sealed partial class PackedReadmeTests(PackedConsumerFeed feed)
{
    private static readonly RealFileSystem FileSystem = new();

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    [Test]
    [Arguments("OpenCodeDotNet.Sdk")]
    [Arguments("OpenCodeDotNet.Sdk.Extensions")]
    public async Task The_Packed_Readme_Should_Link_The_Repository_At_The_Packed_Commit(string packageId)
    {
        var package = await ReadPackageAsync(packageId);
        var link = new Regex(Regex.Escape(package.RepositoryUrl) + @"/(?:blob|tree)/(?<ref>[^/)\s]+)/", RegexOptions.None, MatchTimeout);
        var references = link.Matches(package.Readme).Select(static match => match.Groups["ref"].Value).ToArray();

        await Assert.That(package.Commit).Matches(CommitPattern());
        await Assert.That(package.Readme).DoesNotContain(package.RepositoryUrl + "/blob/master/");
        await Assert.That(package.Readme).DoesNotContain(package.RepositoryUrl + "/tree/master/");
        await Assert.That(references).IsNotEmpty();
        // Only membership and cardinality matter; this expected collection has one item.
        await Assert.That(references.Distinct(StringComparer.Ordinal)).IsEquivalentTo([package.Commit], CollectionOrdering.Any);
    }

    /// <summary>
    /// The packed README is the repository's, byte for byte, apart from the rewritten links. Text that
    /// goes through an MSBuild item list is split at semicolons, has its percent sequences unescaped,
    /// and on Linux and macOS has every backslash turned into a slash; the repository README carries
    /// all three (prose with semicolons, badge URLs with <c>%2F</c>, shell line continuations), so a
    /// write path that loses any of them fails here.
    /// </summary>
    [Test]
    [Arguments("OpenCodeDotNet.Sdk")]
    [Arguments("OpenCodeDotNet.Sdk.Extensions")]
    public async Task The_Packed_Readme_Should_Be_The_Repository_Readme_Apart_From_Its_Links(string packageId)
    {
        var package = await ReadPackageAsync(packageId);
        var repository = new RepositoryRoot(FileSystem).Locate();
        var source = await FileSystem.File.ReadAllTextAsync(FileSystem.Path.Combine(repository, "README.md"));
        var expected = source
            .Replace(package.RepositoryUrl + "/blob/master/", package.RepositoryUrl + "/blob/" + package.Commit + "/", StringComparison.Ordinal)
            .Replace(package.RepositoryUrl + "/tree/master/", package.RepositoryUrl + "/tree/" + package.Commit + "/", StringComparison.Ordinal);

        await Assert.That(source).Contains(';').Because("the comparison must cover a semicolon");
        await Assert.That(PercentSequence().IsMatch(source)).IsTrue().Because("the comparison must cover a percent sequence");
        await Assert.That(source).Contains('\\').Because("the comparison must cover a backslash");
        await Assert.That(package.Readme.TrimEnd('\r', '\n')).IsEqualTo(expected.TrimEnd('\r', '\n'));
    }

    private async Task<PackedReadme> ReadPackageAsync(string packageId)
    {
        var path = FileSystem.Path.Combine(feed.FeedDirectory, packageId + "." + feed.Version + ".nupkg");
        var stream = FileSystem.File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var readme = await ReadEntryAsync(archive, "README.md");
            var nuspec = XDocument.Parse(await ReadEntryAsync(archive, packageId + ".nuspec"));
            var repository = nuspec.Descendants().Single(static element => element.Name.LocalName == "repository");
            return new PackedReadme(
                readme,
                repository.Attribute("url")?.Value ?? string.Empty,
                repository.Attribute("commit")?.Value ?? string.Empty);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException("The package carries no " + name + ".");
        var stream = await entry.OpenAsync();
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, 1000)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("%[0-9A-Fa-f]{2}", RegexOptions.None, 1000)]
    private static partial Regex PercentSequence();

    private sealed record PackedReadme(string Readme, string RepositoryUrl, string Commit);
}
