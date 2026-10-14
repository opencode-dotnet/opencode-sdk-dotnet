using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>What reach is measured over: the document, the selected operations, the families they bind into, and the schema names.</summary>
internal sealed record ReachScope(
    SpecDocument Document,
    IReadOnlyList<SpecOperation> Selected,
    IReadOnlyList<ClientPlan> Clients,
    IReadOnlyDictionary<string, string> TypeNames);
