using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Names the closed converter one <c>Optional&lt;T&gt;</c> instantiation needs. The wrapper is a
/// generic type, and an unbound generic cannot be named in a <c>[JsonConverter]</c> argument, so
/// each instantiation reaching the wire gets its own concrete converter type (ADR-0004).
/// </summary>
internal static class OptionalConverterNamePolicy
{
    public static string ConverterTypeName(TypeReferencePlan inner) =>
        inner is SpecialNumberTypeReferencePlan
            ? "OptionalOfSpecialNumberJsonConverter"
            : $"OptionalOf{Suffix(inner ?? throw new ArgumentNullException(nameof(inner)))}JsonConverter";

    /// <summary>
    /// The instantiation's name in identifier form: the CLR spelling of a primitive, the model
    /// name otherwise, and the same <c>List</c>/<c>Dictionary</c> suffixes the serializer registry
    /// uses for a container accessor.
    /// </summary>
    private static string Suffix(TypeReferencePlan plan) => plan switch
    {
        NamedTypeReferencePlan named => ClrName(named.Name),
        ListTypeReferencePlan list => $"{Suffix(list.ElementType)}List",
        DictionaryTypeReferencePlan dictionary => $"{Suffix(dictionary.ValueType)}Dictionary",
        _ => throw new InvalidOperationException(
            $"No optional converter name exists for plan '{plan.GetType().Name}'; admit the representation first."),
    };

    private static string ClrName(string name) => name switch
    {
        "bool" => "Boolean",
        "double" => "Double",
        "long" => "Int64",
        "string" => "String",
        _ => name,
    };
}
