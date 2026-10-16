using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;

internal sealed class SchemaProjectionTestHost
{
    private readonly GraphKeyBuilder _keys = new();

    public async Task<SchemaProjectionResult> ProjectAsync(SpecScenario scenario)
    {
        var result = await ProjectCoreAsync(scenario);
        return result.Refusal is not null
            ? throw new InvalidOperationException("The schema projection was unexpectedly refused.", result.Refusal)
            : result;
    }

    public async Task<IngestionException> ProjectExpectingRefusalAsync(SpecScenario scenario)
    {
        var result = await ProjectCoreAsync(scenario);
        return result.Refusal ?? throw new InvalidOperationException("The schema projection was expected to be refused.");
    }

    /// <summary>
    /// Reads one component schema as the pinned reader's DOM, for classifiers that walk the raw
    /// document rather than the projected graph.
    /// </summary>
    public static async Task<IOpenApiSchema> LoadSchemaAsync(SpecScenario scenario, string name)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var context = scenario.Build();
        var loaded = await new SpecReader(context.FileSystem).LoadAsync(context.SpecPath, new IngestionErrorCollector(), CancellationToken.None);
        return loaded.Document.Components?.Schemas is { } schemas && schemas.TryGetValue(name, out var schema)
            ? schema
            : throw new InvalidOperationException($"The scenario declares no component schema named '{name}'.");
    }

    private async Task<SchemaProjectionResult> ProjectCoreAsync(SpecScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var context = scenario.Build();
        var errors = new IngestionErrorCollector();
        try
        {
            var loaded = await new SpecReader(context.FileSystem).LoadAsync(context.SpecPath, errors, CancellationToken.None);
            var projector = new SchemaProjector(_keys);
            var state = new ProjectionState(errors, loaded.Document);

            if (loaded.Document.Components?.Schemas is not null)
            {
                foreach (var (name, schema) in loaded.Document.Components.Schemas)
                {
                    _ = projector.Project(schema, _keys.Root(name), string.Empty, state);
                }
            }

            errors.ThrowIfAny();
            return new SchemaProjectionResult(state.SnapshotGraph(), refusal: null);
        }
        catch (IngestionException exception)
        {
            return new SchemaProjectionResult(new Dictionary<string, SchemaNode>(StringComparer.Ordinal), exception);
        }
    }
}
