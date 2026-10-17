using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Read-only per-operation state shared by the facet binders: the ingested document, the
/// operation under bind, its curation, the resolved type-name map, and the error batch.
/// It also owns the one ref-graph <see cref="Resolve(SchemaNode)"/> and the refusal helpers, so every
/// facet attributes its refusals to the same operation identically.
/// </summary>
internal sealed class OperationFacetContext(
    SpecDocument document,
    SpecOperation operation,
    GenerationCuration curation,
    IReadOnlyDictionary<string, string> typeNames,
    BindingErrorCollector errors,
    TypePlanBinder types)
{
    public GenerationCuration Curation { get; } = curation ?? throw new ArgumentNullException(nameof(curation));

    public SpecDocument Document { get; } = document ?? throw new ArgumentNullException(nameof(document));

    public BindingErrorCollector Errors { get; } = errors ?? throw new ArgumentNullException(nameof(errors));

    public SpecOperation Operation { get; } = operation ?? throw new ArgumentNullException(nameof(operation));

    public IReadOnlyDictionary<string, string> TypeNames { get; } = typeNames ?? throw new ArgumentNullException(nameof(typeNames));

    /// <summary>The general type machinery a facet falls through to once its own nominal fast path fails.</summary>
    public TypePlanBinder Types { get; } = types ?? throw new ArgumentNullException(nameof(types));

    public void Refuse(string problem) => Errors.Add(BindingErrorCategory.Operation, Operation.OperationId, problem);

    public OperationFacetContext CreateLocal()
    {
        var localErrors = new BindingErrorCollector();
        return new OperationFacetContext(Document, Operation, Curation, TypeNames, localErrors,
            new TypePlanBinder(Document.Schemas, TypeNames, localErrors));
    }

    public FacetResult<T> Refused<T>() => new FacetResult<T>.Refused(Errors.Snapshot());

    public FacetResult<T> RefuseFacet<T>(string problem)
    {
        Refuse(problem);
        return Refused<T>();
    }

    public FacetResult<T> Publish<T>(FacetResult<T> result, IReadOnlyList<BindingError>? alreadyPublished = null)
    {
        if (result is FacetResult<T>.Refused refused)
        {
            // Only borrowed verdict objects are already published; unrelated equal errors remain independent.
            Errors.Append(refused.Errors.Where(error => alreadyPublished is null
                || !alreadyPublished.Any(original => ReferenceEquals(original, error))));
        }
        return result;
    }

    public string? RefuseNull(string problem)
    {
        Refuse(problem);
        return null;
    }

    public T? RefuseNull<T>(string problem)
        where T : class
    {
        Refuse(problem);
        return null;
    }

    public SchemaNode Resolve(SchemaNode schema) => Resolve(Document, schema);

    /// <summary>Follows a reference chain through the document's components to the schema it ends at; a cycle or a missing target stops it.</summary>
    public static SchemaNode Resolve(SpecDocument document, SchemaNode schema)
    {
        ArgumentNullException.ThrowIfNull(document);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = schema;
        while (current is RefNode reference && visited.Add(reference.Target)
                                            && document.Schemas.TryGetValue(reference.Target, out var target))
        {
            current = target;
        }

        return current;
    }
}
