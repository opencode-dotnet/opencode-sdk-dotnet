using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Counts independent wire uses, not the interface projections that repeat those uses.</summary>
internal static class DocumentationReferenceCounter
{
    public static IReadOnlyDictionary<string, int> Count(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var references = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in plan.Models.OfType<ObjectModelPlan>().SelectMany(static model => model.Properties).Select(static property => property.Type)
                     .Concat(plan.Models.OfType<StructuralUnionModelPlan>().SelectMany(static model => model.Arms).Select(static arm => arm.Type)))
        {
            Add(type, references);
        }

        foreach (var operation in plan.Clients.SelectMany(static client => client.Operations))
        {
            if (operation.RequestBody is { IsBinary: false } body)
            {
                AddName(body.TypeName, references);
            }

            if (operation.Envelope?.PayloadType is { } payload)
            {
                Add(payload, references);
            }

            if (operation.Envelope?.LocationTypeName is { } location)
            {
                AddName(location, references);
            }

            if (operation.Stream is { } stream)
            {
                AddName(stream.PayloadTypeName, references);
            }
        }

        foreach (var name in plan.Unions.SelectMany(static union => union.Variants.Select(static variant => variant.TypeName)
                     .Concat(union.PrefixVariant is { } prefix ? [prefix.TypeName] : [])).Distinct(StringComparer.Ordinal))
        {
            AddName(name, references);
        }

        return references;
    }

    private static void Add(TypeReferencePlan type, Dictionary<string, int> references)
    {
        switch (type)
        {
            case NamedTypeReferencePlan named:
                AddName(named.Name, references);
                break;
            case ListTypeReferencePlan list:
                Add(list.ElementType, references);
                break;
            case DictionaryTypeReferencePlan dictionary:
                Add(dictionary.ValueType, references);
                break;
        }
    }

    private static void AddName(string name, Dictionary<string, int> references) =>
        references[name] = references.GetValueOrDefault(name) + 1;
}
