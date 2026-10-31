using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Output.Abstractions;

namespace OpenCode.Sdk.Tools.Generator;

internal sealed class GenerationCoordinator(
    ISpecIngestion ingestion,
    OperationSelectionLoader selectionLoader,
    CurationLoader curationLoader,
    ISpecBinder binder,
    PendingOperationBindabilityProbe pendingProbe,
    SourceWatchLoader sourceWatchLoader,
    IGenerationWriter writer)
{
    private readonly ISpecIngestion _ingestion = ingestion ?? throw new ArgumentNullException(nameof(ingestion));
    private readonly OperationSelectionLoader _selectionLoader = selectionLoader ?? throw new ArgumentNullException(nameof(selectionLoader));
    private readonly CurationLoader _curationLoader = curationLoader ?? throw new ArgumentNullException(nameof(curationLoader));
    private readonly ISpecBinder _binder = binder ?? throw new ArgumentNullException(nameof(binder));
    private readonly PendingOperationBindabilityProbe _pendingProbe = pendingProbe ?? throw new ArgumentNullException(nameof(pendingProbe));
    private readonly SourceWatchLoader _sourceWatchLoader = sourceWatchLoader ?? throw new ArgumentNullException(nameof(sourceWatchLoader));
    private readonly IGenerationWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<GenerationReport> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Curation loads first: its operation-identity rows are an ingestion input (ADR-0013),
        // so their static validity is checked before the document is read.
        var curation = await _curationLoader.LoadAsync(request.CurationPath, cancellationToken).ConfigureAwait(false);
        await RefuseUnwatchedEvidenceAsync(curation, cancellationToken).ConfigureAwait(false);
        var operationIdentities = OperationIdentityPolicy.BuildMap(curation);
        var document = await _ingestion.IngestAsync(request.SpecPath, operationIdentities, cancellationToken).ConfigureAwait(false);
        var selection = await _selectionLoader.LoadAsync(request.ProfilePath, cancellationToken).ConfigureAwait(false);

        var plan = _binder.Bind(document, selection, curation);
        PartialMarkerComposer.RefuseUnplacedModels(plan.Placement);

        var pending = plan
            .PendingOperations
            .Select(static operation => operation.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Probe pending operations with the selection-path binder so a newly bindable operation
        // changes the committed marker and receives review rather than remaining silently unselected.
        var pendingMarks = pending.Length > 0 ? _pendingProbe.Probe(document, pending) : [];
        // Declined operations are not pending either, but they are probed all the same: the
        // decline claims a standing wall, and the probe is what proves that claim. A declined
        // operation the probe finds bindable refuses the run — the wall it cites is gone, so the
        // decision behind the row has to be reviewed rather than outlive its reason — unless its
        // row rests on upstream's client omission instead of a wall.
        var declined = plan.DeclinedOperations;
        var declinedMarks = declined.Count > 0
            ? _pendingProbe.Probe(document, [.. declined.Select(static operation => operation.OperationId)])
            : [];
        PartialMarkerComposer.RefuseBindableDeclines(declined, declinedMarks);

        // Transport-owned operations are not pending — the binder keeps them out — so they are
        // never probed; the marker lists them beside the pending map as fingerprint-pinned. The
        // marker outlives a cleared pending set: while any operation stands outside the generated
        // surface by decision, the file is the committed record of which ones and why.
        var marker = pending.Length > 0 || declined.Count > 0 || plan.TransportOwnedOperationIds.Count > 0
            ? PartialMarkerComposer.Compose(plan.SelectedOperationIds, pendingMarks, declined, declinedMarks, plan.TransportOwnedOperationIds)
            : null;

        var reach = ModelReachReport.Compose(plan.Placement, plan.ModelReach);
        var writeResult = await _writer
            .WriteAsync(CreateWriteRequest(request, plan, marker, reach), cancellationToken)
            .ConfigureAwait(false);

        return new GenerationReport
        {
            SelectedOperationIds = plan.SelectedOperationIds,
            PendingOperationIds = pending,
            TransportOwnedOperationIds = plan.TransportOwnedOperationIds,
            DeclinedOperationIds = [.. declined.Select(static operation => operation.OperationId)],
            WriteResult = writeResult,
            ModelsReachedBeyondOwner = reach.ReachedBeyondOwner,
        };
    }

    /// <summary>
    /// The admitted family folders are the plan's own container names, and the model area folders
    /// the placement's own areas — never an open glob.
    /// </summary>
    private static GenerationWriteRequest CreateWriteRequest(GenerationRequest request, EmitPlan plan, string? marker,
        ModelReachReport reach) =>
        new()
        {
            OutputRoot = request.OutputRoot,
            ProjectPath = request.ProjectPath,
            Sources = SourceEmitter.Emit(plan),
            FamilyFolders =
            [
                .. plan.Clients
                    .SelectMany(static client => client.Operations)
                    .Select(static operation => operation.RouteContainerName)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ],
            ModelAreaFolders =
            [
                .. plan.Placement.Homes.Values
                    .Where(static home => home.Area is not null)
                    .Select(GeneratedPlacement.ModelAreaFolder)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ],
            ImplicitAliases = plan.ImplicitAliases.Aliases,
            PartialMarkerContent = marker,
            ReachReportContent = reach.Content,
            Verify = request.Verify,
        };

    /// <summary>
    /// The source watch is read for one check only: a query parameter type row's evidence must be
    /// a watched file. It never reaches ingestion or emission.
    /// </summary>
    private async Task RefuseUnwatchedEvidenceAsync(GenerationCuration curation, CancellationToken cancellationToken)
    {
        if (curation.QueryParameterTypes.Count is 0)
        {
            return;
        }

        var watch = await _sourceWatchLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        var errors = new BindingErrorCollector();
        QueryParameterEvidencePolicy.Check(curation, watch, errors);
        errors.ThrowIfAny();
    }
}
