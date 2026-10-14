using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The checks a generated structural union converter runs before it selects one of several object
/// arms. Effect's union decode (<c>SchemaAST.ts</c>, <c>Union.getParser</c>) narrows candidates by
/// literal sentinels and then takes the first member in declaration order that decodes; the
/// converter mirrors that with the member's required keys and literal-constrained properties, and
/// only the selected member's deep parse follows. Keys the member does not declare are ignored,
/// as Effect ignores excess properties.
/// </summary>
internal static class StructuralUnionClaim
{
    /// <summary>Returns whether the object carries <paramref name="property"/>.</summary>
    public static bool Has(JsonElement root, string property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return root.TryGetProperty(property, out _);
    }

    /// <summary>Returns whether the object carries <paramref name="property"/> as one of the admitted strings.</summary>
    public static bool IsText(JsonElement root, string property, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(values);
        return root.TryGetProperty(property, out var value) && MatchesText(value, values);
    }

    /// <summary>Returns whether <paramref name="property"/> is absent or one of the admitted strings.</summary>
    public static bool AllowsText(JsonElement root, string property, params string[] values)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(values);
        return !root.TryGetProperty(property, out var value) || MatchesText(value, values);
    }

    /// <summary>Returns whether the object carries <paramref name="property"/> as one of the admitted booleans.</summary>
    public static bool IsBoolean(JsonElement root, string property, params bool[] values)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(values);
        return root.TryGetProperty(property, out var value) && MatchesBoolean(value, values);
    }

    /// <summary>Returns whether <paramref name="property"/> is absent or one of the admitted booleans.</summary>
    public static bool AllowsBoolean(JsonElement root, string property, params bool[] values)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(values);
        return !root.TryGetProperty(property, out var value) || MatchesBoolean(value, values);
    }

    private static bool MatchesText(JsonElement value, string[] values) =>
        value.ValueKind is JsonValueKind.String && Array.IndexOf(values, value.GetString()) >= 0;

    private static bool MatchesBoolean(JsonElement value, bool[] values) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False && Array.IndexOf(values, value.GetBoolean()) >= 0;
}
