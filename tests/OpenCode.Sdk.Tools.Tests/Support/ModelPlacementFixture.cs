using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// Places every model of a hand-built plan in one slice, with the plan's body requests at its root. Emitter tests exercise emission, not
/// ownership, so their fixtures need a placement but not the ownership rule behind it.
/// </summary>
internal static class ModelPlacementFixture
{
    public const string Slice = "Example";

    public static ModelPlacement SingleSlice(IEnumerable<ModelPlan> models, IEnumerable<UnionPlan>? unions = null,
        IEnumerable<HoistedInterfacePlan>? hoistedInterfaces = null)
    {
        ArgumentNullException.ThrowIfNull(models);

        var modelPlans = models.ToArray();
        var names = modelPlans
            .Select(static model => model.Name)
            .Concat(modelPlans.OfType<StructuralUnionModelPlan>().Select(static model => model.KindTypeName))
            .Concat((unions ?? []).SelectMany(static union => new[] { union.Name, union.UnknownTypeName, }))
            .Concat((hoistedInterfaces ?? []).Select(static hoisted => hoisted.Name));
        var home = TypeHome.Family(Slice);
        return new ModelPlacement { Homes = names.Distinct(StringComparer.Ordinal).ToDictionary(static name => name, _ => home, StringComparer.Ordinal), };
    }

    public static ModelPlacement SingleSlice(IEnumerable<UnionPlan> unions) => SingleSlice([], unions);

    public static ModelPlacement SingleSlice(IEnumerable<StructuralUnionModelPlan> unions) => SingleSlice(unions.Cast<ModelPlan>());

    public static ModelPlacement SingleSlice(IEnumerable<HoistedInterfacePlan> hoistedInterfaces) => SingleSlice([], null, hoistedInterfaces);

    public static EmitPlan WithSingleSlice(this EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var bodyRequests = plan.Clients
            .SelectMany(static client => client.Operations)
            .Where(static operation => operation.RequestBody is { IsBinary: false })
            .Select(static operation => operation.RequestBody!.TypeName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return plan with
        {
            Placement = SingleSlice(plan.Models, plan.Unions, plan.HoistedInterfaces) with { BodyRequests = [.. bodyRequests] },
        };
    }

    /// <summary>
    /// Places the models a scenario's curation leaves unplaced, with their kit members, in the
    /// fixture slice and keeps every placement the ownership rule made.
    /// </summary>
    public static EmitPlan WithUnplacedInSingleSlice(this EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Placement.Unplaced.Count is 0)
        {
            return plan;
        }

        var unplaced = plan.Placement.Unplaced.ToHashSet(StringComparer.Ordinal);
        var fill = SingleSlice(
            plan.Models.Where(model => unplaced.Contains(model.Name)),
            plan.Unions.Where(union => unplaced.Contains(union.Name)),
            plan.HoistedInterfaces.Where(hoisted => unplaced.Contains(hoisted.Name)));
        var homes = new Dictionary<string, TypeHome>(plan.Placement.Homes, StringComparer.Ordinal);
        foreach (var (name, home) in fill.Homes)
        {
            homes[name] = home;
        }

        return plan with { Placement = plan.Placement with { Homes = homes, Unplaced = [] } };
    }
}
