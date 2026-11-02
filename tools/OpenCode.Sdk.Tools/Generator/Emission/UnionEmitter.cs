using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<UnionPlan> unions, ModelPlacement placement, GeneratedNamespacePlan namespaces)
        => Emit(unions, placement, namespaces, new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal));

    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<UnionPlan> unions, ModelPlacement placement,
        GeneratedNamespacePlan namespaces, IReadOnlyDictionary<string, IReadOnlySet<string>> readerArms)
    {
        ArgumentNullException.ThrowIfNull(unions);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(namespaces);
        ArgumentNullException.ThrowIfNull(readerArms);
        var result = new List<GeneratedSource>(unions.Count * 4);
        foreach (var union in unions.OrderBy(static union => union.Name, StringComparer.Ordinal))
        {
            result.Add(UnionInterfaceEmitter.EmitBase(union, GeneratedPlacement.ModelPathOf(placement, union.Name), namespaces));
            result.Add(UnionUnknownEmitter.EmitUnknown(union, GeneratedPlacement.ModelPathOf(placement, union.UnknownTypeName), namespaces));
            result.Add(UnionDispatchEmitter.EmitConverter(union, placement, namespaces, readerArms));
            result.Add(UnionCarrierConverterEmitter.EmitCarrierConverter(union, placement, namespaces));
        }

        return Array.AsReadOnly([.. result]);
    }
}
