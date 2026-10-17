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
    BindingErrorCollector Errors);
