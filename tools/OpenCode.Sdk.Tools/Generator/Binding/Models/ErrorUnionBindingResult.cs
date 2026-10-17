namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record ErrorUnionBindingResult(
    UnionBindingResult Union,
    IReadOnlyDictionary<string, UnionVariantPlan> Variants,
    IReadOnlyDictionary<string, BindingError> Refusals);
