using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Refuses a bound type named like a hand-written internal type. Generated internal types share the
/// internal namespace with the hand-written ones and every generated file imports it, so such a twin
/// would not compile or would make the name ambiguous. The bind refuses every name it knows; the
/// emitter still refuses a converter name derived later.
/// </summary>
internal static class InternalNameCollisionCheck
{
    public static void Check(OwnershipCandidates candidates, IReadOnlyList<ClientPlan> clients, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(errors);

        var operations = clients.SelectMany(static client => client.Operations).ToArray();
        var names = candidates.Models.Select(static model => model.Name)
            .Concat(candidates.Models.OfType<StructuralUnionModelPlan>().Select(static model => model.KindTypeName))
            .Concat(candidates.Unions.SelectMany(static union => new[] { union.Name, union.UnknownTypeName }))
            .Concat(candidates.HoistedInterfaces.Select(static hoisted => hoisted.Name))
            .Concat(clients.Select(static client => client.Name))
            .Concat(operations.Select(static operation => operation.QueryRequest?.TypeName))
            .Concat(operations.Select(static operation => operation.Envelope?.ResponseTypeName))
            .Concat(operations.Select(static operation => operation.Envelope?.AdapterTypeName))
            .Concat(operations.Select(static operation => operation.Envelope?.EnvelopeDtoTypeName))
            .Concat(operations.Select(static operation => operation.Stream?.AdapterTypeName))
            .OfType<string>();
        foreach (var name in names
                     .Where(static name => ReservedNamePolicy.InternalTypeNames.Contains(name))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Naming, name, $"type name '{name}' collides with the hand-written internal type '{name}'");
        }
    }
}
