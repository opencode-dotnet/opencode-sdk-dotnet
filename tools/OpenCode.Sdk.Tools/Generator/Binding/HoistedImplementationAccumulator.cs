using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Collects what each record gains from hoisting — the carriers it implements and the members it
/// answers explicitly — and writes them back onto the bound models in one pass, so the hoist
/// walk never rebuilds a model plan per member.
/// </summary>
internal sealed class HoistedImplementationAccumulator
{
    private readonly Dictionary<string, List<string>> _carriers = [with(StringComparer.Ordinal)];

    private readonly Dictionary<string, List<HoistedImplementationPlan>> _explicits = [with(StringComparer.Ordinal)];

    public void AddCarrier(string modelName, string interfaceName)
    {
        var carriers = Entry(_carriers, modelName);
        if (!carriers.Contains(interfaceName, StringComparer.Ordinal))
        {
            carriers.Add(interfaceName);
        }
    }

    public void AddExplicitImplementation(string modelName, HoistedImplementationPlan implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        Entry(_explicits, modelName).Add(implementation);
    }

    public IReadOnlyList<ModelPlan> Apply(IReadOnlyList<ModelPlan> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (_carriers.Count is 0 && _explicits.Count is 0)
        {
            return models;
        }

        return Array.AsReadOnly([.. models.Select(Apply)]);
    }

    private ModelPlan Apply(ModelPlan model)
    {
        if (model is not ObjectModelPlan objectModel)
        {
            return model;
        }

        var carriers = _carriers.TryGetValue(objectModel.Name, out var names) ? names : [];
        var explicits = _explicits.TryGetValue(objectModel.Name, out var implementations) ? implementations : [];
        if (carriers.Count is 0 && explicits.Count is 0)
        {
            return model;
        }

        return objectModel with
        {
            ImplementedHoistedInterfaceNames = carriers,
            ExplicitHoistedImplementations = explicits,
        };
    }

    private static List<TValue> Entry<TValue>(Dictionary<string, List<TValue>> source, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!source.TryGetValue(key, out var entry))
        {
            entry = [];
            source[key] = entry;
        }

        return entry;
    }
}
