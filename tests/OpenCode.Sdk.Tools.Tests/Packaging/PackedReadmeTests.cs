using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>
/// The README a package carries links the repository at the commit the package was built from,
/// the commit its own nuspec records, so the files it points to match the package. The
/// repository's README links master; only the packed copy is rewritten, its raw.githubusercontent.com
/// images included. CI status badges keep
/// pointing at master, because they report master's state rather than link a file.
/// </summary>
[Category(TestCategories.Packaging)]
[ClassDataSource<PackedConsumerFeed>(Shared = SharedType.PerTestSession)]
public sealed partial class PackedReadmeTests(PackedConsumerFeed feed)
{
    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Arguments("OpenCodeDotNet.Sdk")]
    [Arguments("OpenCodeDotNet.Sdk.Extensions")]
    public async Task The_Packed_Readme_Should_Link_The_Repository_At_The_Packed_Commit(string packageId)
    {
        var package = await ReadPackageAsync(packageId);
        var raw = RawUrl(package.RepositoryUrl);
        var link = new Regex(Regex.Escape(package.RepositoryUrl) + @"/(?:blob|tree)/(?<ref>[^/)\s]+)/", RegexOptions.None, Regex.InfiniteMatchTimeout);
        var image = new Regex(Regex.Escape(raw) + @"/(?<ref>[^/)\s]+)/", RegexOptions.None, Regex.InfiniteMatchTimeout);
        var references = link.Matches(package.Readme).Select(static match => match.Groups["ref"].Value).ToArray();
        var imageReferences = image.Matches(package.Readme).Select(static match => match.Groups["ref"].Value).ToArray();

        await Assert.That(package.Commit).Matches(CommitPattern());
        await Assert.That(package.Readme).DoesNotContain(package.RepositoryUrl + "/blob/master/");
        await Assert.That(package.Readme).DoesNotContain(package.RepositoryUrl + "/tree/master/");
        await Assert.That(package.Readme).DoesNotContain(raw + "/master/");
        await Assert.That(references).IsNotEmpty();
        await Assert.That(imageReferences).IsNotEmpty();
        // Only membership and cardinality matter; each expected collection has one item.
        await Assert.That(references.Distinct(StringComparer.Ordinal)).IsEquivalentTo([package.Commit], CollectionOrdering.Any);
        await Assert.That(imageReferences.Distinct(StringComparer.Ordinal)).IsEquivalentTo([package.Commit], CollectionOrdering.Any);
    }

    /// <summary>
    /// The packed README is the repository's, byte for byte, apart from the rewritten links. Text that
    /// goes through an MSBuild item list is split at semicolons, has its percent sequences unescaped,
    /// and on Linux and macOS has every backslash turned into a slash. The repository README carries
    /// the first two (prose with semicolons, badge URLs with <c>%2F</c>), so a write path that loses
    /// either fails here; it has no backslash, so the fixture test below covers that one.
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
            .Replace(package.RepositoryUrl + "/tree/master/", package.RepositoryUrl + "/tree/" + package.Commit + "/", StringComparison.Ordinal)
            .Replace(RawUrl(package.RepositoryUrl) + "/master/", RawUrl(package.RepositoryUrl) + "/" + package.Commit + "/", StringComparison.Ordinal);

        await Assert.That(source).Contains(';').Because("the comparison must cover a semicolon");
        await Assert.That(PercentSequence().IsMatch(source)).IsTrue().Because("the comparison must cover a percent sequence");
        await Assert.That(package.Readme.TrimEnd('\r', '\n')).IsEqualTo(expected.TrimEnd('\r', '\n'));
    }

    /// <summary>
    /// The task the pack runs to write the README keeps a backslash, a semicolon and a percent
    /// sequence byte for byte and rewrites only the master links. It runs here on a fixture through
    /// the repository's own <c>Directory.Build.targets</c>, because the repository README carries no
    /// backslash. On Linux and macOS a write path that passes the text through an MSBuild item list
    /// turns the backslash into a slash and fails here.
    /// </summary>
    [Test]
    public async Task The_Readme_Writer_Should_Keep_Backslashes_Semicolons_And_Percent_Sequences()
    {
        // The writer replaces text only, so the repository URL needs no scheme to be rewritten.
        const string repositoryUrl = "github.com/opencode-dotnet/opencode-sdk-dotnet";
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string fixture = "dotnet nuget add source https://example.invalid/index.json \\\n  --name feed\nA list; with a semicolon and %2F in a [link](" + repositoryUrl + "/blob/master/docs/guide/README.md).\n";
        var repository = new RepositoryRoot(FileSystem).Locate();
        var root = FileSystem.Path.Combine(FileSystem.Path.GetTempPath(), "opencode-packed-readme-" + Guid.NewGuid().ToString("N"));
        _ = FileSystem.Directory.CreateDirectory(root);
        try
        {
            FileSystem.File.Copy(FileSystem.Path.Combine(repository, "global.json"), FileSystem.Path.Combine(root, "global.json"));
            await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(root, "README.md"), fixture, new UTF8Encoding(false));
            await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(root, "write.proj"), $$"""
                <Project>
                  <Import Project="{{FileSystem.Path.Combine(repository, "Directory.Build.targets")}}" />
                  <Target Name="Write">
                    <WritePackedReadme Source="$(MSBuildThisFileDirectory)README.md" Destination="$(MSBuildThisFileDirectory)packed/README.md" RepositoryUrl="{{repositoryUrl}}" Commit="{{commit}}" />
                  </Target>
                </Project>
                """);

            var written = await new CliWrapProcessRunner().RunAsync(
                "dotnet", ["msbuild", "write.proj", "-t:Write", "-nologo", "-nodeReuse:false"], root, CancellationToken.None);
            await Assert.That(written.ExitCode).IsEqualTo(0).Because(Encoding.UTF8.GetString(written.StandardOutput) + written.StandardError);

            var packed = await FileSystem.File.ReadAllTextAsync(FileSystem.Path.Combine(root, "packed", "README.md"));
            await Assert.That(packed).IsEqualTo(fixture.Replace(repositoryUrl + "/blob/master/", repositoryUrl + "/blob/" + commit + "/", StringComparison.Ordinal));
        }
        finally
        {
            FileSystem.Directory.Delete(root, recursive: true);
        }
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

    /// <summary>
    /// An image's GitHub address is <c>raw.githubusercontent.com/&lt;owner&gt;/&lt;repo&gt;/&lt;ref&gt;/&lt;path&gt;</c>,
    /// the form NuGet.org renders.
    /// </summary>
    private static string RawUrl(string repositoryUrl) =>
        repositoryUrl.Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.Ordinal);

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("%[0-9A-Fa-f]{2}", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex PercentSequence();

    private sealed record PackedReadme(string Readme, string RepositoryUrl, string Commit);
}
