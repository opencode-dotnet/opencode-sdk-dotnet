using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;

/// <summary>Real Git in a private clone; only Bun's generated document is controlled.</summary>
internal sealed class AutocrlfRefreshScenario : IProcessRunner, IDisposable
{
    private readonly RealFileSystem _fileSystem = new();
    private readonly CliWrapProcessRunner _git = new();
    private readonly string _repository;
    private readonly string _root;
    private readonly string _clone;
    private int _generation;
    private LoadedPatch _patch = null!;
    public byte[] CheckoutBytes { get; private set; } = [];
    public byte[] BlobBytes { get; private set; } = [];

    public AutocrlfRefreshScenario()
    {
        _repository = new RepositoryRoot(_fileSystem).Locate();
        _root = _fileSystem.Path.Combine(_repository, ".scratchpad", "autocrlf", Guid.NewGuid().ToString("N"));
        _clone = _fileSystem.Path.Combine(_root, "clone");
        _ = _fileSystem.Directory.CreateDirectory(_root);
    }

    public async Task<GeneratedSnapshot> GenerateAsync(bool autocrlf)
    {
        if (!_fileSystem.Directory.Exists(_clone))
        {
            _ = await GitAsync(["clone", "--no-hardlinks", "--no-checkout", _fileSystem.Path.Combine(_repository, SnapshotPaths.Submodule), _clone], _root);
            var manifestPath = _fileSystem.Path.Combine(_repository, SnapshotPaths.PatchesRoot, "001-restore-sse-payloads.json");
            var manifest = JsonSerializer.Deserialize(await _fileSystem.File.ReadAllTextAsync(manifestPath), ToolJsonContext.Default.PatchManifest)!;
            _patch = new LoadedPatch
            {
                ManifestName = _fileSystem.Path.GetFileName(manifestPath),
                Manifest = manifest,
                PatchPath = _fileSystem.Path.Combine(_repository, SnapshotPaths.PatchesRoot, manifest.Patch),
            };
        }

        _ = await GitAsync(["config", "core.autocrlf", autocrlf ? "true" : "false"], _clone);
        var commit = (await GitAsync(["rev-parse", "HEAD"], _clone)).StandardOutputText.Trim();
        BlobBytes = (await GitAsync(["show", $"{commit}:{_patch.Manifest.Touches[0]}"], _clone)).StandardOutput;
        _generation = 0;
        return await new SnapshotGenerator(_fileSystem, this, NullLogger<SnapshotGenerator>.Instance)
            .GenerateAsync(commit, _fileSystem.Path.Combine(_root, "candidate"), [_patch], CancellationToken.None);
    }

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        if (fileName == "git")
        {
            var directory = workingDirectory == _fileSystem.Path.GetFullPath(SnapshotPaths.Submodule) ? _clone : workingDirectory;
            return await _git.RunAsync(fileName, arguments, directory, cancellationToken);
        }

        if (fileName == "bun" && arguments.SequenceEqual(["install", "--frozen-lockfile", "--ignore-scripts"], StringComparer.Ordinal))
        {
            CheckoutBytes = await _fileSystem.File.ReadAllBytesAsync(_fileSystem.Path.Combine(workingDirectory, _patch.Manifest.Touches[0]), cancellationToken);
        }
        else if (fileName == "bun" && arguments.SequenceEqual(["run", "generate"], StringComparer.Ordinal))
        {
            var patched = _generation++ > 0;
            var document = RefreshScenarioData.DocumentBytes(spec =>
            {
                foreach (var component in _patch.Manifest.RepairPredicate.Components)
                {
                    _ = spec.WithSchema(component, schema =>
                    {
                        _ = schema.Type("string");
                        if (patched)
                        {
                            _ = schema.ContentSchema("application/json", payload => payload.Type("string"));
                        }
                    });
                }
            });
            await _fileSystem.File.WriteAllBytesAsync(_fileSystem.Path.Combine(workingDirectory, "openapi.json"), document, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException($"Unexpected process: {fileName} {string.Join(' ', arguments)}");
        }

        return ScriptedProcessRunner.Ok();
    }

    private async Task<ProcessResult> GitAsync(IReadOnlyList<string> arguments, string directory)
    {
        var result = await _git.RunAsync("git", arguments, directory, CancellationToken.None);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError);
        }

        return result;
    }

    public void Dispose() => _ = BestEffortDelete.TryDeleteTree(_fileSystem, _root);
}
