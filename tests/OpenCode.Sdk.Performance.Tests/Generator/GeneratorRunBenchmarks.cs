#if NET
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;
using OpenCode.Sdk.Tools;
using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Output.Abstractions;
using Testably.Abstractions;

namespace OpenCode.Sdk.Performance.Tests.Generator;

/// <summary>
/// The generator's whole runs and writing row on the real pinned input: every in-process stage plus
/// the writer and manifest without formatting, the <c>dotnet format</c> pass over freshly emitted
/// sources, <c>generate --verify</c> end to end, and writing alone. Whole runs take seconds, so the job
/// monitors a few single invocations instead of piloting thousands.
/// </summary>
/// <remarks>
/// <para>
/// The unformatted run writes into a copy of <c>src/OpenCode.Sdk</c> under a no-op formatter; less
/// <see cref="GeneratorStageBenchmarks"/>' rows, it is the writer plus the run's own preparation
/// (curation, source watch, identity map) and orchestration. It runs through the capture, with one
/// capture field assignment per seconds-long run. The format row puts the emitted bytes
/// back into the repository's generated tree before each iteration, outside the measurement, so it
/// formats what a real run formats (the emitted bytes are the settled copy's, put back by file copy);
/// its allocations are this process's wrapper around the
/// subprocess, not the subprocess's own. The verify row runs <c>generate --verify</c> on the
/// repository's tree, which rewrites it with identical bytes.
/// </para>
/// <para>
/// Because the format and verify rows rewrite the repository's generated tree, the setup refuses to
/// run unless <see cref="RewriteVariable"/> is <see cref="RewriteAllowed"/> and the tree is clean:
/// the baseline procedure sets the variable in a disposable clone. An interrupted run leaves the
/// tree half-written (emitted but unformatted); <c>generate</c> restores it. The setup also runs
/// from the repository root, because the run reads <c>spec/source-watch.json</c> relative to its
/// working directory and BenchmarkDotNet starts each benchmark in its generated project's folder.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5)]
public class GeneratorRunBenchmarks : IDisposable
{
    /// <summary>The variable that lets this class rewrite the repository's generated tree.</summary>
    public const string RewriteVariable = "OPENCODE_SDK_BENCHMARKS_REWRITE_TREE";

    /// <summary>The one value <see cref="RewriteVariable"/> takes.</summary>
    public const string RewriteAllowed = "allow";

    private readonly RealFileSystem _fileSystem = new();
    private ServiceProvider? _services;
    private ServiceProvider? _unformattedServices;
    private GeneratorInput? _input;
    private string? _copyRoot;
    private string? _workingDirectory;
    private GenerationCoordinator? _coordinator;
    private GenerationCoordinator? _unformattedCoordinator;
    private GenerationWriter? _writer;
    private GenerationWriteRequest? _writeRequest;
    private IProjectFormatter? _formatter;
    private string[] _generatedSources = [];

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _input = GeneratorInput.Locate();
        await RequireDisposableTreeAsync(_input.Root).ConfigureAwait(false);
        _workingDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _input.Root;

        _services = ToolApp.CreateServices().BuildServiceProvider();
        _unformattedServices = ToolApp
            .CreateServices(static services => services
                .AddSingleton<IProjectFormatter, NoOpProjectFormatter>()
                .AddSingleton<GenerationWriter>()
                .AddSingleton<CapturingGenerationWriter>()
                .AddSingleton<IGenerationWriter>(static provider => provider.GetRequiredService<CapturingGenerationWriter>()))
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
        _ = await RunUnformattedAsync(_copyRoot).ConfigureAwait(false);
        RefuseChanges(await RunUnformattedAsync(_copyRoot).ConfigureAwait(false), "the unformatted run");
        _writer = _unformattedServices.GetRequiredService<GenerationWriter>();
        _writeRequest = _unformattedServices.GetRequiredService<CapturingGenerationWriter>().Request
            ?? throw new InvalidOperationException("The coordinator did not write any output.");
    }

    /// <summary>Every in-process stage, the writer and the manifest, into a copy of the generated tree.</summary>
    [Benchmark]
    public Task GenerateWithoutFormattingAsync() => RunUnformattedAsync(_copyRoot!);

    /// <summary>
    /// Writes sources and the manifest over unchanged emitted output in the copy, without formatting.
    /// The process's peak working set includes setup's coordinator runs, so it is not the write's own peak.
    /// </summary>
    [Benchmark]
    public Task WriteAsync() => _writer!.WriteAsync(_writeRequest!, CancellationToken.None);

    /// <summary>
    /// Puts the emitted, unformatted sources back into the repository's tree for the next format
    /// iteration: the settled copy holds exactly the emitted bytes, so a file copy restores them
    /// without running the generator inside the hook.
    /// </summary>
    [IterationSetup(Target = nameof(FormatEmittedAsync))]
    public void RestoreEmittedSources()
    {
        foreach (var source in _generatedSources)
        {
            _fileSystem.File.Copy(
                _fileSystem.Path.Combine(_copyRoot!, source), _fileSystem.Path.Combine(_input!.OutputRoot, source), overwrite: true);
        }
    }

    /// <summary>The <c>dotnet format</c> subprocess over freshly emitted sources, as a real run formats them.</summary>
    [Benchmark]
    public Task FormatEmittedAsync() => _formatter!.FormatAsync(_input!.ProjectPath, _generatedSources, CancellationToken.None);

    /// <summary>
    /// <c>generate --verify</c> end to end on the repository's own generated tree.
    /// The pipeline's verify flag changes only the reported result, not the work, so this row also
    /// measures <c>generate</c>'s whole-run cost, not the CLI's differing exit code and messages.
    /// </summary>
    [Benchmark]
    public Task VerifyEndToEndAsync() => RunVerifiedAsync();

    /// <summary>Restores the committed tree after the format row and refuses one the generator would still change.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        try
        {
            if (_coordinator is not null)
            {
                RefuseChanges(await RunVerifiedAsync().ConfigureAwait(false), "the closing generate --verify");
            }
        }
        finally
        {
            Dispose();
        }
    }

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

        if (_workingDirectory is not null)
        {
            Environment.CurrentDirectory = _workingDirectory;
            _workingDirectory = null;
        }
    }

    private Task<GenerationReport> RunUnformattedAsync(string outputRoot) =>
        _unformattedCoordinator!.GenerateAsync(_input!.Request(outputRoot, verify: true), CancellationToken.None);

    private Task<GenerationReport> RunVerifiedAsync() =>
        _coordinator!.GenerateAsync(_input!.Request(_input.OutputRoot, verify: true), CancellationToken.None);

    /// <summary>
    /// Refuses a working checkout: the format and verify rows rewrite its generated tree, so they run
    /// only where that was asked for, in a tree with nothing of its own to lose.
    /// </summary>
    private static async Task RequireDisposableTreeAsync(string root)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RewriteVariable), RewriteAllowed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The generator run benchmarks rewrite the repository's generated tree. Run them only in a disposable clean clone, with {RewriteVariable}={RewriteAllowed}.");
        }

        var gitPath = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve("git").Path;
        using var git = Process.Start(new ProcessStartInfo(gitPath)
        {
            ArgumentList = { "-C", root, "status", "--porcelain" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("git did not start.");
        var standardError = git.StandardError.ReadToEndAsync();
        var status = await git.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        _ = await standardError.ConfigureAwait(false);
        await git.WaitForExitAsync().ConfigureAwait(false);
        if (git.ExitCode != 0 || status.Length > 0)
        {
            throw new InvalidOperationException(
                "The generator run benchmarks need a clean tree, because they rewrite its generated sources; '" + root + "' has changes or is not a git checkout.");
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
                + "benchmark only a clone whose generated tree matches the generator.");
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

}
#endif
