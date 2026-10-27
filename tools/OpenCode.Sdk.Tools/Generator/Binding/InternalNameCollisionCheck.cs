using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Refuses a bound type named like a hand-written internal type. Generated internal types share the
/// internal namespace with the hand-written ones and every generated file imports it, so such a twin
/// would not compile or would make the name ambiguous. The bind refuses every generated type name,
/// converters included; the emitter re-checks the files it writes.
/// </summary>
internal static class InternalNameCollisionCheck
{
    public static void Check(GeneratedTypeNameInventory inventory, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(errors);

        foreach (var name in inventory.Names
                     .Where(static name => ReservedNamePolicy.InternalTypeNames.Contains(name))
                     .Order(StringComparer.Ordinal))
        {
            errors.Add(BindingErrorCategory.Naming, name, $"type name '{name}' collides with the hand-written internal type '{name}'");
        }
    }
}
