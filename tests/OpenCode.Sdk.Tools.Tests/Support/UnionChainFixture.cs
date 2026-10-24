using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class UnionChainFixture
{
    public static IReadOnlyList<UnionPlan> Build(IReadOnlyList<UnionPlan> unions) => UnionChainBuilder.Build(unions);

    public static EmitPlan Resolve(EmitPlan plan)
    {
        var errors = new BindingErrorCollector();
        var resolved = DescriptionCurationBinder.Apply(SpecBinder.ResolveEmissionData(plan), [], errors);
        errors.ThrowIfAny();
        return resolved;
    }
}
