using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class UnionEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(IReadOnlyList<UnionPlan> unions, ModelPlacement placement, GeneratedNamespacePlan namespaces)
    {
        ArgumentNullException.ThrowIfNull(unions);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(namespaces);
        var result = new List<GeneratedSource>(unions.Count * 4);
        foreach (var union in unions.OrderBy(static union => union.Name, StringComparer.Ordinal))
        {
            result.Add(UnionInterfaceEmitter.EmitBase(union, GeneratedPlacement.ModelPathOf(placement, union.Name), namespaces));
            result.Add(UnionUnknownEmitter.EmitUnknown(union, GeneratedPlacement.ModelPathOf(placement, union.UnknownTypeName), namespaces));
            result.Add(UnionDispatchEmitter.EmitConverter(union, placement, namespaces));
            result.Add(UnionCarrierConverterEmitter.EmitCarrierConverter(union, placement, namespaces));
        }

        return Array.AsReadOnly([.. result]);
    }
}
