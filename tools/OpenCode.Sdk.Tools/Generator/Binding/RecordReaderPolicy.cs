using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Selects literal, closed record arms in the current union-reader rollout.</summary>
internal static class RecordReaderPolicy
{
    public static IReadOnlyDictionary<string, ObjectModelPlan> Select(IReadOnlyList<ModelPlan> models, IReadOnlyList<UnionPlan> unions,
        out IReadOnlyDictionary<string, IReadOnlySet<string>> readerArms)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(unions);
        var objects = models.OfType<ObjectModelPlan>().ToDictionary(static model => model.Name, StringComparer.Ordinal);
        var selected = new SortedDictionary<string, ObjectModelPlan>(StringComparer.Ordinal);
        var arms = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var union in unions)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            arms.Add(union.Name, names);
            if (!InRollout(union))
            {
                continue;
            }

            foreach (var name in union.Variants.Where(variant => !variant.IsNestedUnion && variant.MarkerWireName == union.MarkerWireName)
                         .Select(static variant => variant.TypeName))
            {
                if (!objects.TryGetValue(name, out var model))
                {
                    throw new InvalidOperationException($"Selected reader arm '{union.Name}.{name}' has no bound object model.");
                }

                if (!model.EmitsExtensionData)
                {
                    selected[model.Name] = model;
                    names.Add(model.Name);
                }
            }
        }

        readerArms = arms;
        return selected;
    }

    private static bool InRollout(UnionPlan union) => union.Name is "ISessionMessageToolState" or "IToolContent";
}
