using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using TUnit.Core.Interfaces;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

/// <summary>Completed test syntax shared by all repository source guards in one test session.</summary>
public sealed class TestSourceCorpusSession : IAsyncInitializer
{
    private readonly IFileSystem _fileSystem;
    private readonly IProcessRunner _runner;
    private string? _root;
    private TestSourceCorpus? _corpus;

    /// <summary>Creates the real-repository fixture used by TUnit.</summary>
    public TestSourceCorpusSession()
        : this(new RealFileSystem(), new CliWrapProcessRunner(), null)
    {
    }

    internal TestSourceCorpusSession(IFileSystem fileSystem, IProcessRunner runner, string? root)
    {
        _fileSystem = fileSystem;
        _runner = runner;
        _root = root;
    }

    internal TestSourceCorpus Corpus => _corpus ?? throw new InvalidOperationException("The corpus is not initialized.");

    internal string Root => _root ?? throw new InvalidOperationException("The corpus is not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var root = _root ??= new RepositoryRoot(_fileSystem).Locate();
        // TUnit initialization has no cancellation token; the fixture outlives any one test.
        var paths = await new RepositoryFileSet(_fileSystem).EnumerateAsync(root, _runner, CancellationToken.None);
        _corpus = await new TestSourceCorpusReader(_fileSystem).ReadAsync(root, paths, CancellationToken.None);
    }

    internal async Task<IReadOnlyList<string>> CheckAsync(string name, IReadOnlyList<RepositoryGateSite> sites, string rule, CancellationToken cancellationToken)
    {
        var path = "tests/OpenCode.Sdk.Tools.Tests/RepositoryGates/" + name + ".txt";
        var text = await _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(Root, path), cancellationToken);
        return SiteList.Compare(path, SiteList.Parse(path, text), sites, rule);
    }
}
