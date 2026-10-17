using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Promotes onto a marked union's interface every property its members declare in the same
/// shape, so a consumer reads the shared part of a union without switching over concrete types
/// (ADR-0011). The rule is mechanical over the pinned document: it invents nothing, refuses
/// nothing that already binds, and only ever adds members the members themselves already carry.
/// </summary>
/// <remarks>
/// Bases are hoisted before the unions that derive from them, so a nested union inherits the
/// outer promise instead of hiding it. A union with fewer than two members shares nothing across
/// arms and is left alone.
/// </remarks>
internal static class UnionMemberHoistBinder
{
    public static UnionHoistPlan Bind(IReadOnlyList<UnionPlan> unions, IReadOnlyList<ModelPlan> models,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(unions);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(errors);

        var context = CreateContext(unions, models, curation, errors);
        var hoisted = new Dictionary<string, IReadOnlyList<HoistedMemberPlan>>(StringComparer.Ordinal);
        foreach (var union in OrderByChainDepth(unions, context.Unions))
        {
            hoisted[union.Name] = HoistUnion(union, context, hoisted);
        }

        context.Carriers.ReportUnusedRows();
        return new UnionHoistPlan
        {
            Unions = [.. unions.Select(union => hoisted[union.Name] is { Count: > 0 } members ? union with { HoistedMembers = members } : union)],
            Models = context.Implementations.Apply(models),
            Interfaces = context.Carriers.Interfaces,
            Claims = context.Carriers.Claims,
        };
    }

    private static HoistContext CreateContext(IReadOnlyList<UnionPlan> unions, IReadOnlyList<ModelPlan> models,
        GenerationCuration curation, BindingErrorCollector errors)
    {
        var objectModels = models.OfType<ObjectModelPlan>().ToDictionary(static model => model.Name, StringComparer.Ordinal);
        var implementations = new HoistedImplementationAccumulator();
        // One policy instance answers both the union interface and every carrier interface, so the
        // two can never disagree about which record property satisfies a declared member.
        var satisfaction = new HoistedMemberSatisfactionPolicy(
            models.OfType<EnumModelPlan>().Select(static model => model.Name).ToHashSet(StringComparer.Ordinal));
        return new HoistContext(
            unions.ToDictionary(static union => union.Name, StringComparer.Ordinal),
            objectModels,
            new HoistedMemberIdentityPolicy(objectModels),
            satisfaction,
            new HoistedCarrierComposer(objectModels, curation.HoistedMemberNames, satisfaction, implementations, errors),
            implementations,
            errors);
    }

    private static ReadOnlyCollection<HoistedMemberPlan> HoistUnion(UnionPlan union, HoistContext context,
        IReadOnlyDictionary<string, IReadOnlyList<HoistedMemberPlan>> hoisted)
    {
        var result = new List<HoistedMemberPlan>();
        if (ResolveMembers(union, context) is { } members)
        {
            var dispatched = UnionDispatchPropertyPolicy.Collect(union, context.Unions);
            var inherited = InheritedNames(union, context.Unions, hoisted);
            foreach (var candidate in members[0].Properties)
            {
                if (dispatched.Contains(candidate.WireName) || inherited.Contains(candidate.Name))
                {
                    continue;
                }

                var owned = SharedProperties(members, candidate, context.Identity);
                if (owned is null || ResolveMember(union, candidate, owned, context) is not { } member)
                {
                    continue;
                }

                result.Add(member);
                RecordImplementations(union, members, owned, member.Type, context);
            }
        }

        return Array.AsReadOnly([.. result]);
    }

    /// <summary>The records the union dispatches to, or null when it has too few to share anything.</summary>
    private static List<ObjectModelPlan>? ResolveMembers(UnionPlan union, HoistContext context)
    {
        var names = UnionMemberCollector.Collect(union, context.Unions);
        if (names.Count < 2)
        {
            return null;
        }

        var members = new List<ObjectModelPlan>(names.Count);
        foreach (var name in names)
        {
            if (!context.Models.TryGetValue(name, out var member))
            {
                context.Errors.Add(BindingErrorCategory.Schema, union.Name, $"union member '{name}' has no bound record");
                return null;
            }

            members.Add(member);
        }

        return members;
    }

    /// <summary>Each member's own property when all of them declare the candidate identically.</summary>
    private static List<ModelPropertyPlan>? SharedProperties(List<ObjectModelPlan> members, ModelPropertyPlan candidate,
        HoistedMemberIdentityPolicy identity)
    {
        var owned = new List<ModelPropertyPlan>(members.Count);
        foreach (var member in members)
        {
            var property = member.Properties.FirstOrDefault(current =>
                string.Equals(current.WireName, candidate.WireName, StringComparison.Ordinal));
            if (property is null || !identity.Identical(candidate, property))
            {
                return null;
            }

            owned.Add(property);
        }

        return owned;
    }

    /// <summary>
    /// The member the interface declares: the shared type widened to nullable, or the carrier
    /// interface when each variant promotes its own record for the same shape. Nullable because
    /// the union's unknown carrier holds a raw payload and materializes no typed member.
    /// </summary>
    private static HoistedMemberPlan? ResolveMember(UnionPlan union, ModelPropertyPlan candidate,
        List<ModelPropertyPlan> owned, HoistContext context)
    {
        var promoted = PromotedRecordSet.Resolve(owned, context.Models);
        var carrier = promoted is null ? null : context.Carriers.Resolve(union.Name, union.ConceptName, candidate, promoted);
        if (promoted is not null && carrier is null)
        {
            return null;
        }

        var type = carrier is null
            ? candidate.Type with { IsNullable = true }
            : new NamedTypeReferencePlan
            {
                Name = carrier,
                IsNullable = true,
                JsonNullRepresentation = candidate.Type.JsonNullRepresentation,
            };
        return new HoistedMemberPlan
        {
            WireName = candidate.WireName,
            Name = candidate.Name,
            IsRequired = candidate.IsRequired,
            Type = type,
            Description = candidate.Description,
        };
    }

    private static void RecordImplementations(UnionPlan union, List<ObjectModelPlan> members,
        List<ModelPropertyPlan> owned, TypeReferencePlan declared, HoistContext context)
    {
        for (var index = 0; index < members.Count; index++)
        {
            if (!context.Satisfaction.RequiresExplicitImplementation(owned[index], declared))
            {
                continue;
            }

            context.Implementations.AddExplicitImplementation(members[index].Name, new HoistedImplementationPlan
            {
                InterfaceName = union.Name,
                MemberName = owned[index].Name,
                MemberType = declared,
                PropertyName = owned[index].Name,
                ReadsOptionalValue = owned[index].EmitsOptionalWrapper,
            });
        }
    }

    private static HashSet<string> InheritedNames(UnionPlan union, IReadOnlyDictionary<string, UnionPlan> unions,
        IReadOnlyDictionary<string, IReadOnlyList<HoistedMemberPlan>> hoisted)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { union.MarkerName };
        for (var current = union.BaseTypeName; current is not null && unions.TryGetValue(current, out var outer);
             current = outer.BaseTypeName)
        {
            _ = result.Add(outer.MarkerName);
            if (hoisted.TryGetValue(outer.Name, out var members))
            {
                result.UnionWith(members.Select(static member => member.Name));
            }
        }

        return result;
    }

    private static IEnumerable<UnionPlan> OrderByChainDepth(IReadOnlyList<UnionPlan> unions,
        IReadOnlyDictionary<string, UnionPlan> byName) =>
        unions
            .OrderBy(union => ChainDepth(union, byName))
            .ThenBy(static union => union.Name, StringComparer.Ordinal);

    private static int ChainDepth(UnionPlan union, IReadOnlyDictionary<string, UnionPlan> byName)
    {
        var depth = 0;
        for (var current = union.BaseTypeName; current is not null && byName.TryGetValue(current, out var outer) && depth < byName.Count;
             current = outer.BaseTypeName)
        {
            depth++;
        }

        return depth;
    }

    private sealed record HoistContext(
        IReadOnlyDictionary<string, UnionPlan> Unions,
        IReadOnlyDictionary<string, ObjectModelPlan> Models,
        HoistedMemberIdentityPolicy Identity,
        HoistedMemberSatisfactionPolicy Satisfaction,
        HoistedCarrierComposer Carriers,
        HoistedImplementationAccumulator Implementations,
        BindingErrorCollector Errors);
}
