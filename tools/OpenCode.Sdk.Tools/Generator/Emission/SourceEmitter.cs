using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

internal static class SourceEmitter
{
    public static IReadOnlyList<GeneratedSource> Emit(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sources = new List<GeneratedSource>();
        sources.AddRange(ModelEmitter.Emit(plan));
        sources.AddRange(OptionalConverterEmitter.Emit(plan.Models, plan.Namespaces));
        sources.AddRange(StructuralUnionEmitter.Emit([.. plan.Models.OfType<StructuralUnionModelPlan>()], plan.Placement, plan.Namespaces));
        sources.AddRange(UnionEmitter.Emit(plan.Unions, plan.Placement, plan.Namespaces));
        sources.AddRange(HoistedInterfaceEmitter.Emit(plan.HoistedInterfaces, plan.Placement));
        sources.AddRange(RegistryEmitter.Emit(plan.Registry, plan.Namespaces));
        if (plan.Clients.Count > 0)
        {
            sources.AddRange(RoutesEmitter.Emit(plan.Clients, plan.QueryEnumModels, plan.Namespaces));
            sources.AddRange(QueryRequestEmitter.Emit(plan.Clients, plan.Namespaces));
            sources.AddRange(EnvelopeDtoEmitter.Emit(plan.Clients, plan.Namespaces));
            sources.AddRange(EnvelopeEmitter.Emit(plan.Clients, plan.Namespaces));
            sources.AddRange(ResponseAdapterEmitter.Emit(plan.Clients, plan.Namespaces));
            sources.AddRange(StreamAdapterEmitter.Emit(plan.Clients, plan.Namespaces));
            sources.AddRange(ClientEmitter.Emit(plan.Clients, plan.Namespaces));
        }

        var duplicate = sources
            .GroupBy(static source => source.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Skip(1).Any());

        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Multiple emitters produced '{duplicate.Key}'.");
        }

        // A file is named after the type it declares (up to the first dot of a partial part).
        var reserved = sources
            .Select(static source => source.RelativePath.Split('/')[^1].Split('.')[0])
            .FirstOrDefault(static typeName => ReservedNamePolicy.InternalTypeNames.Contains(typeName));
        if (reserved is not null)
        {
            throw new InvalidOperationException($"Generated type '{reserved}' collides with the hand-written internal type '{reserved}'.");
        }

        return Array.AsReadOnly([.. sources.OrderBy(static source => source.RelativePath, StringComparer.Ordinal)]);
    }
}
