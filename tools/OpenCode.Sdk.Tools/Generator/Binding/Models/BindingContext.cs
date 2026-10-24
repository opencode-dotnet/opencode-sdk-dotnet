using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record BindingContext
{
    public BindingContext(SpecDocument document, ReachableSchemaSet reachable, GenerationCuration curation, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        Document = document;
        Reachable = reachable;
        Curation = curation;
        Errors = errors;
    }

    public SpecDocument Document { get; init; }
    public ReachableSchemaSet Reachable { get; init; }
    public GenerationCuration Curation { get; init; }
    public BindingErrorCollector Errors { get; init; }
}
