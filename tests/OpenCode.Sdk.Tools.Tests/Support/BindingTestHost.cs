using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal sealed class BindingTestHost
{
    private readonly SpecBinder _binder = new();

    /// <summary>The production binder pipeline, exposed for callers that drive it themselves (e.g. the pending-operation probe).</summary>
    public ISpecBinder Binder => _binder;

    /// <summary>
    /// Binds through the production pipeline. Scenario specs name their models freely, so the
    /// models a scenario's curation leaves unplaced go to the fixture slice; ownership itself is
    /// tested against <see cref="ModelOwnershipPolicy"/> and the generate command.
    /// </summary>
    public EmitPlan Bind(SpecDocument document, OperationSelection selection, GenerationCuration curation)
    {
        return _binder.Bind(document, selection, curation).WithUnplacedInSingleSlice();
    }

    public static async Task<SpecDocument> IngestAsync(SpecScenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var context = scenario.Build();
        return await new SpecIngestion(context.FileSystem).IngestAsync(context.SpecPath, cancellationToken);
    }

    public async Task<EmitPlan> BindPinnedAsync(CancellationToken cancellationToken = default)
    {
        var (document, selection, curation) = await LoadPinnedInputsAsync(cancellationToken);
        return Bind(document, selection, curation);
    }

    /// <summary>
    /// Loads the pinned fixture inputs the way the production coordinator does: the fixture
    /// curation's operation-identity map is an ingestion input, never an afterthought.
    /// </summary>
    public static async Task<(SpecDocument Document, OperationSelection Selection, GenerationCuration Curation)> LoadPinnedInputsAsync(
        CancellationToken cancellationToken = default)
    {
        var fileSystem = new RealFileSystem();
        var fixtureRoot = fileSystem.Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var curation = await new CurationLoader(fileSystem)
            .LoadAsync(fileSystem.Path.Combine(fixtureRoot, "curation.json"), cancellationToken);
        var document = await new SpecIngestion(fileSystem)
            .IngestAsync(
                fileSystem.Path.Combine(fixtureRoot, "openapi.json"),
                OperationIdentityPolicy.BuildMap(curation),
                cancellationToken);
        var selection = await new OperationSelectionLoader(fileSystem)
            .LoadAsync(fileSystem.Path.Combine(fixtureRoot, "generation-profile.txt"), cancellationToken);
        return (document, selection, curation);
    }

    public static async Task<SpecDocument> IngestPinnedAsync(CancellationToken cancellationToken = default)
    {
        var (document, _, _) = await LoadPinnedInputsAsync(cancellationToken);
        return document;
    }
}
