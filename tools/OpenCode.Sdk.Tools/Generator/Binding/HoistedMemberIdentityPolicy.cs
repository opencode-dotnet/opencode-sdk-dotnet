using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Decides whether two members declare one property in the same shape. Identity requires the
/// same wire name, the same .NET name, the same required-ness, the same nullability, and the
/// same represented type; it ignores the value of a non-dispatch literal, because a
/// <c>const</c> or single-value <c>enum</c> that discriminates nothing is an ordinary primitive
/// on both sides (ADR-0004). The tri-state <c>Optional&lt;T&gt;</c> wrapper is part of that shape
/// too: one member declaring it and another not are two different CLR types, so the property is
/// not shared. Documentation-only keywords never reach the bound plan, so they are ignored by
/// construction.
/// </summary>
/// <remarks>
/// A property whose members each promote their own inline object is identical when those
/// records are, which is what lets one hoisted carrier stand for all of them. The recursion
/// walks those records under the same rule and refuses to loop on a cycle.
/// </remarks>
internal sealed class HoistedMemberIdentityPolicy(IReadOnlyDictionary<string, ObjectModelPlan> models)
{
    private readonly IReadOnlyDictionary<string, ObjectModelPlan> _models =
        models ?? throw new ArgumentNullException(nameof(models));

    private readonly HashSet<string> _comparing = [with(StringComparer.Ordinal)];

    public bool Identical(ModelPropertyPlan left, ModelPropertyPlan right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(left.WireName, right.WireName, StringComparison.Ordinal)
               && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
               && left.IsRequired == right.IsRequired
               && left.EmitsOptionalWrapper == right.EmitsOptionalWrapper
               && TypesIdentical(left.Type, right.Type);
    }

    private bool TypesIdentical(TypeReferencePlan left, TypeReferencePlan right)
    {
        if (left == right)
        {
            return true;
        }

        if (left is not NamedTypeReferencePlan first || right is not NamedTypeReferencePlan second)
        {
            return false;
        }

        // Nullability and null representation are part of the declared shape even when the two
        // sides name different promoted records.
        return first.IsNullable == second.IsNullable
               && first.JsonNullRepresentation == second.JsonNullRepresentation
               && _models.TryGetValue(first.Name, out var leftModel)
               && _models.TryGetValue(second.Name, out var rightModel)
               && RecordsIdentical(leftModel, rightModel);
    }

    private bool RecordsIdentical(ObjectModelPlan left, ObjectModelPlan right)
    {
        if (left.Properties.Count != right.Properties.Count)
        {
            return false;
        }

        // A revisited pair is a cycle inside a comparison that has not failed yet.
        var pair = string.Concat(left.Name, "\0", right.Name);
        if (!_comparing.Add(pair))
        {
            return true;
        }

        try
        {
            return left.Properties.Zip(right.Properties).All(properties => Identical(properties.First, properties.Second));
        }
        finally
        {
            _ = _comparing.Remove(pair);
        }
    }
}
