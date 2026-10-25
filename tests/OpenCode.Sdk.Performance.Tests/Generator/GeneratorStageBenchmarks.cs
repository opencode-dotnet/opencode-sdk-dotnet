#if NET
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Performance.Tests.Support;
using OpenCode.Sdk.Tools;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Performance.Tests.Generator;

/// <summary>
/// The generator's in-process stages on the real pinned input: ingestion, binding with curation,
/// and emission, each from the previous stage's output prepared once, so a row is that stage's own
/// cost. Writing, formatting and the whole run are <see cref="GeneratorRunBenchmarks"/>.
/// </summary>
/// <remarks>
/// The run's bindability probe is a measured stage over the pinned plan's pending and declined
/// operations. The rows return <see cref="object"/> or <see cref="Task"/>:
/// the stage types are internal to the tool, and a benchmark method is public.
/// The class carries its own .NET 10 job, so the invocation passes no <c>--runtimes</c> and its rows
/// keep the <c>.NET 10.0</c> label that joins the archived before-set.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class GeneratorStageBenchmarks : IDisposable
{
    private ServiceProvider? _services;
    private ISpecIngestion? _ingestion;
    private ISpecBinder? _binder;
    private PendingOperationBindabilityProbe? _probe;
    private GeneratorInput? _input;
    private IReadOnlyDictionary<string, string>? _identities;
    private SpecDocument? _document;
    private OperationSelection? _selection;
    private GenerationCuration? _curation;
    private EmitPlan? _plan;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _input = GeneratorInput.Locate();
        _services = ToolApp.CreateServices().BuildServiceProvider();
        _ingestion = _services.GetRequiredService<ISpecIngestion>();
        _binder = _services.GetRequiredService<ISpecBinder>();
        _probe = _services.GetRequiredService<PendingOperationBindabilityProbe>();

        _curation = await _services.GetRequiredService<CurationLoader>().LoadAsync(_input.CurationPath, CancellationToken.None).ConfigureAwait(false);
        _identities = OperationIdentityPolicy.BuildMap(_curation);
        _document = await IngestDocumentAsync().ConfigureAwait(false);
        _selection = await _services.GetRequiredService<OperationSelectionLoader>().LoadAsync(_input.ProfilePath, CancellationToken.None).ConfigureAwait(false);
        _plan = BindPlan();
        if (SourceEmitter.Emit(_plan).Count == 0)
        {
            throw new InvalidOperationException("The pinned input emitted no sources; the stage rows would measure nothing.");
        }
    }

    /// <summary>Reads, validates and normalizes the pinned OpenAPI document.</summary>
    [Benchmark]
    public Task IngestAsync() => IngestDocumentAsync();

    /// <summary>Binds the selected operations and their reachable schemas into the emit plan, under the curation.</summary>
    [Benchmark]
    public object Bind() => BindPlan();

    /// <summary>Probes the bindability of pending and declined operations through the selection-path binder.</summary>
    [Benchmark]
    public object Probe()
    {
        var pending = _plan!.PendingOperations.Select(static operation => operation.OperationId).Order(StringComparer.Ordinal).ToArray();
        var pendingMarks = pending.Length > 0 ? _probe!.Probe(_document!, pending) : [];
        var declined = _plan.DeclinedOperations;
        var declinedMarks = declined.Count > 0
            ? _probe!.Probe(_document!, [.. declined.Select(static operation => operation.OperationId)])
            : [];
        return new[] { pendingMarks, declinedMarks };
    }

    /// <summary>Emits every generated source from the plan, unformatted.</summary>
    [Benchmark]
    public object Emit() => SourceEmitter.Emit(_plan!);

    private Task<SpecDocument> IngestDocumentAsync() => _ingestion!.IngestAsync(_input!.SpecPath, _identities!, CancellationToken.None);

    private EmitPlan BindPlan() => _binder!.Bind(_document!, _selection!, _curation!);

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
    }
}
#endif
