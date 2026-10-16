using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions;

/// <summary>Ingests the pinned OpenAPI document into the generator's semantic model.</summary>
internal interface ISpecIngestion
{
    /// <summary>
    /// Loads, validates, and projects the spec; refuses with batched located errors. The
    /// operation-identity map carries curation's reason-bearing repairs for upstream identity
    /// defects (subject id to intended id); an unconsumed subject refuses so stale rows retire.
    /// </summary>
    public Task<SpecDocument> IngestAsync(string specPath, IReadOnlyDictionary<string, string> operationIdentities,
        CancellationToken cancellationToken);
}
