#if NET
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Performance.Tests.Support;
using OpenCode.Sdk.Tools;
using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Output.Abstractions;
using Testably.Abstractions;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The generator's whole runs on the real pinned input. The run without formatting writes into a
/// copy of <c>src/OpenCode.Sdk</c> under a no-op formatter, so it is every in-process stage plus the
/// writer and the manifest; less <see cref="GeneratorStageBenchmarks"/>' rows, it is the writer's
/// own cost. Formatting runs the real <c>dotnet format</c> subprocess over the generated sources
/// alone; its allocations are another process's, so only its time counts. The verify run is
/// <c>generate --verify</c> end to end: it rewrites the repository's own generated tree with
/// identical bytes, so the setup refuses a tree the generator would change. Like the tool itself, an
/// interrupted verify run leaves that tree half-written (emitted but unformatted); running
/// <c>generate</c> restores it. Each run takes seconds, so the job monitors a few single invocations
/// instead of piloting thousands.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5)]
public class GeneratorRunBenchmarks : IDisposable
{
    private readonly RealFileSystem _fileSystem = new();
    private ServiceProvider? _services;
    private ServiceProvider? _unformattedServices;
    private GeneratorInput? _input;
    private string? _copyRoot;
    private GenerationCoordinator? _coordinator;
    private GenerationCoordinator? _unformattedCoordinator;
    private IProjectFormatter? _formatter;
    private string[] _generatedSources = [];

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _input = GeneratorInput.Locate();
        _services = ToolApp.CreateServices().BuildServiceProvider();
        _unformattedServices = ToolApp
            .CreateServices(static services => services.AddSingleton<IProjectFormatter, NoOpProjectFormatter>())
            .BuildServiceProvider();
        _coordinator = _services.GetRequiredService<GenerationCoordinator>();
        _unformattedCoordinator = _unformattedServices.GetRequiredService<GenerationCoordinator>();
        _formatter = _services.GetRequiredService<IProjectFormatter>();
        _generatedSources = await GeneratorInput.ManifestFilesAsync(_fileSystem, _input.OutputRoot).ConfigureAwait(false);

        _copyRoot = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "opencode-generator-benchmark-" + Guid.NewGuid().ToString("N"));
        CopyGeneratedTree(_input.OutputRoot, _copyRoot);

        RefuseChanges(await RunVerifiedAsync().ConfigureAwait(false), "generate --verify");
        // The copy holds formatted sources; the first unformatted run replaces them with the emitted
        // bytes, and every later run finds the copy settled.
        _ = await RunUnformattedAsync().ConfigureAwait(false);
        RefuseChanges(await RunUnformattedAsync().ConfigureAwait(false), "the unformatted run");
    }

    /// <summary>Every in-process stage, the writer and the manifest, into a copy of the generated tree.</summary>
    [Benchmark]
    public Task GenerateWithoutFormattingAsync() => RunUnformattedAsync();

    /// <summary>The <c>dotnet format</c> subprocess over the generated sources, already formatted.</summary>
    [Benchmark]
    public Task FormatAsync() => _formatter!.FormatAsync(_input!.ProjectPath, _generatedSources, CancellationToken.None);

    /// <summary><c>generate --verify</c> end to end on the repository's own generated tree.</summary>
    [Benchmark]
    public Task VerifyEndToEndAsync() => RunVerifiedAsync();

    private Task<GenerationReport> RunUnformattedAsync() =>
        _unformattedCoordinator!.GenerateAsync(_input!.Request(_copyRoot!, verify: true), CancellationToken.None);

    private Task<GenerationReport> RunVerifiedAsync() =>
        _coordinator!.GenerateAsync(_input!.Request(_input.OutputRoot, verify: true), CancellationToken.None);

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _services?.Dispose();
        _unformattedServices?.Dispose();
        if (_copyRoot is not null && _fileSystem.Directory.Exists(_copyRoot))
        {
            _fileSystem.Directory.Delete(_copyRoot, recursive: true);
        }
    }

    /// <summary>
    /// A run that changes files would measure a different workload on every iteration (the first
    /// rewrites, the rest find it settled), so only a tree the generator already matches qualifies.
    /// </summary>
    private static void RefuseChanges(GenerationReport report, string run)
    {
        var result = report.WriteResult;
        if (result.HasChanges)
        {
            throw new InvalidOperationException(
                $"{run} would create {result.CreatedPaths.Count}, change {result.ChangedPaths.Count} and delete {result.DeletedPaths.Count} file(s); "
                + "benchmark only a worktree whose generated tree matches the generator.");
        }
    }

    private void CopyGeneratedTree(string source, string destination)
    {
        foreach (var file in _fileSystem.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = _fileSystem.Path.GetRelativePath(source, file);
            // GetRelativePath answers with the platform's own separator.
            var first = relative.Split(_fileSystem.Path.DirectorySeparatorChar, 2)[0];
            if (first is "bin" or "obj")
            {
                continue;
            }

            var target = _fileSystem.Path.Combine(destination, relative);
            _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(target)!);
            _fileSystem.File.Copy(file, target);
        }
    }

    /// <summary>Formatting is measured on its own row, so the run without it leaves the sources as emitted.</summary>
    private sealed class NoOpProjectFormatter : IProjectFormatter
    {
        public Task FormatAsync(string projectPath, IReadOnlyList<string> sourcePaths, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
#endif
