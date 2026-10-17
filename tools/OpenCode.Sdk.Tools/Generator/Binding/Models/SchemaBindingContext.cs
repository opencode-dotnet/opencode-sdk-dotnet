using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record SchemaBindingContext(
    SpecDocument Document,
    ReachableSchemaSet Reachable,
    IReadOnlySet<string> ResponseRootKeys,
    IReadOnlySet<string> StreamCauseKeys,
    IReadOnlyDictionary<string, string> TypeNames,
    TypePlanBinder Types,
    SchemaInhabitationPolicy Inhabitation,
    BindingErrorCollector Errors)
{
    public static SchemaBindingContext Create(SpecDocument document, ReachableSchemaSet reachable,
        IReadOnlyDictionary<string, string> typeNames, BindingErrorCollector errors) =>
        new(document, reachable, reachable.ResponseRootKeys.ToHashSet(StringComparer.Ordinal), reachable.StreamCauseKeys.ToHashSet(StringComparer.Ordinal),
            typeNames, new TypePlanBinder(document.Schemas, typeNames, errors), new SchemaInhabitationPolicy(document.Schemas), errors);
}
