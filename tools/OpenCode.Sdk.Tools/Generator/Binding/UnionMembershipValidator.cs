using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class UnionMembershipValidator
{
    public static void Validate(IReadOnlyList<ObjectModelPlan> models, IReadOnlyList<UnionPlan> unions,
        BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(unions);
        ArgumentNullException.ThrowIfNull(errors);

        var unionsByName = unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
        foreach (var model in models)
        {
            ValidateModel(model, unionsByName, errors);
        }
    }

    private static void ValidateModel(ObjectModelPlan model, Dictionary<string, UnionPlan> unions,
        BindingErrorCollector errors)
    {
        var markers = new Dictionary<string, LiteralKind>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var implementedName in model.ImplementedUnionNames)
        {
            var currentName = implementedName;
            var isDeclaredMembership = true;
            while (visited.Add(currentName))
            {
                if (!unions.TryGetValue(currentName, out var current))
                {
                    if (isDeclaredMembership)
                    {
                        errors.Add(BindingErrorCategory.Schema, model.Name,
                            $"union membership references absent union '{currentName}'");
                    }

                    break;
                }

                if (markers.TryGetValue(current.MarkerWireName, out var existingKind)
                    && existingKind != current.MarkerKind)
                {
                    errors.Add(BindingErrorCategory.Schema, model.Name,
                        $"union memberships declare marker '{current.MarkerWireName}' with different kinds");
                    break;
                }

                markers[current.MarkerWireName] = current.MarkerKind;
                if (current.BaseTypeName is null)
                {
                    break;
                }

                currentName = current.BaseTypeName;
                isDeclaredMembership = false;
            }
        }
    }
}
