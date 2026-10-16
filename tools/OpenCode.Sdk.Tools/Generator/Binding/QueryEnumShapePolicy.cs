using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Splits enum-valued query parameters into the two populations the generator treats
/// differently: the value sets the hand-written spine already owns, and every other set,
/// which becomes a generated C# enum. Reachability, name resolution, and the query binder
/// all ask this one policy, so a schema can never be a model in one pass and a spine value
/// in the next. The split is by value set, never by parameter name (ADR-0013).
/// </summary>
internal static class QueryEnumShapePolicy
{
    /// <summary>
    /// Recognizes list-order and boolean-text value sets so query binding uses their
    /// existing representations instead of emitting enum models.
    /// </summary>
    public static QueryValueKind? ResolveSpineKind(EnumNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node switch
        {
            { Values: ["asc", "desc"], Format: null } => QueryValueKind.ListOrder,
            { Values: ["true", "false"] or ["false", "true"], Format: null } => QueryValueKind.BooleanText,
            _ => null,
        };
    }

    /// <summary>
    /// Resolves the graph key of the enum a query parameter binds to a generated C# enum,
    /// or <see langword="null"/> when the parameter is not enum-valued, carries a format, or
    /// spells a spine value set. A parameter admitting null resolves exactly like one that
    /// does not: null-admission is dialect ceremony the emitted optionality already carries.
    /// The unwrap happens once, before the reference walk and never inside it, so a
    /// <c>$ref</c> to a component that is itself <c>anyOf[enum, null]</c> resolves to the
    /// nullable rather than the enum and binds nowhere. That fails closed - the operation
    /// refuses by name instead of typing a property against a model that was not generated -
    /// and the pin declares no such component, so the case stays refused until one appears to
    /// prove the widening.
    /// </summary>
    public static string? ResolveModelKey(SchemaNode schema, IReadOnlyDictionary<string, SchemaNode> graph)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(graph);

        var candidate = schema switch
        {
            NullableNode { Format: null } nullable => nullable.Inner,
            NullableNode => null,
            _ => schema,
        };

        // Ingestion promotes every inline enum into the graph, so the enum a parameter binds
        // always arrives behind a reference; a key is what the model and its name hang on.
        string? key = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (candidate is RefNode reference && visited.Add(reference.Target)
                                              && graph.TryGetValue(reference.Target, out var target))
        {
            key = reference.Target;
            candidate = target;
        }

        return candidate is EnumNode { Format: null } node && ResolveSpineKind(node) is null ? key : null;
    }
}
