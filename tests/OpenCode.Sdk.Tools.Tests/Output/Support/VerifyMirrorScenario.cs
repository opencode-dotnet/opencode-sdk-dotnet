using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Output.Abstractions;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Output.Support;

internal sealed class VerifyMirrorScenario(MockFileSystem fileSystem)
{
    internal GenerationWriter CreateWriter(IProjectFormatter? formatter = null,
        IProcessRunner? runner = null, ILogger<GenerationMirror>? logger = null)
    {
        var placement = new PlacementValidator(fileSystem);
        return new GenerationWriter(fileSystem, placement, new ManifestStore(fileSystem, placement),
            new StagedWrite(fileSystem, placement, formatter ?? new RecordingProjectFormatter(fileSystem)),
            new GenerationMirror(fileSystem, runner ?? GenerationTestData.MirrorProcesses(fileSystem), logger ?? NullLogger<GenerationMirror>.Instance));
    }

    /// <summary>Assembles one write request over the shared test roots; flags default off.</summary>
    internal static GenerationWriteRequest Request(IReadOnlyList<GeneratedSource> sources,
        IReadOnlyList<string>? familyFolders = null, string? partialMarkerContent = null, bool verify = false,
        IReadOnlyDictionary<string, string>? implicitAliases = null, IReadOnlyList<string>? modelAreaFolders = null,
        string? reachReportContent = null) =>
        new()
        {
            OutputRoot = GenerationTestData.OutputRoot,
            ProjectPath = GenerationTestData.ProjectPath,
            Sources = sources,
            FamilyFolders = familyFolders ?? [],
            ModelAreaFolders = modelAreaFolders ?? [],
            PartialMarkerContent = partialMarkerContent,
            ReachReportContent = reachReportContent,
            ImplicitAliases = implicitAliases ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Verify = verify,
        };

    internal async Task<Dictionary<string, byte[]>> ReadTreeAsync(string? root = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in fileSystem.Directory.EnumerateFiles(root ?? fileSystem.Directory.GetCurrentDirectory(), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            files.Add(path, await fileSystem.File.ReadAllBytesAsync(path));
        }
        return files;
    }

    internal async Task AssertTreeUnchangedAsync(IReadOnlyDictionary<string, byte[]> before, string? root = null)
    {
        var after = await ReadTreeAsync(root);
        await Assert.That(after.Keys).IsEquivalentTo(before.Keys, CollectionOrdering.Matching);
        foreach (var (path, bytes) in before)
        {
            await Assert.That(after[path]).IsEquivalentTo(bytes, CollectionOrdering.Matching);
        }
    }

    internal string OwnedPath(string relativePath) =>
        fileSystem.Path.Combine(GenerationTestData.OutputRoot, relativePath);

    internal async Task AssertReadOnlyVerifyAsync(IReadOnlyDictionary<string, byte[]> before)
    {
        await AssertTreeUnchangedAsync(before);
        await Assert.That(fileSystem.Directory.EnumerateDirectories(fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "opencode-sdk-generate-verify"))).IsEmpty();
    }

}
