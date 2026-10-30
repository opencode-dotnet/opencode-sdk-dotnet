using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>
/// Recognizes the special-number union: one plain number beside the "NaN", "Infinity" and
/// "-Infinity" spellings, whole or flattened into a wider union.
/// </summary>
internal static class SpecialNumberPolicy
{
    private static readonly HashSet<string> SpecialNumberValues = new(StringComparer.Ordinal)
    {
        "NaN",
        "Infinity",
        "-Infinity",
    };

    /// <summary>Whether a union is the special-number union, nested one level as the v2 dialect writes it or not.</summary>
    public static bool IsWhole(IEnumerable<IOpenApiSchema> branches)
    {
        var numberBranches = 0;
        var specialBranches = 0;
        return CountSpecialNumberBranches(branches, allowNested: true, ref numberBranches, ref specialBranches)
               && numberBranches is 1
               && specialBranches > 0;
    }

    /// <summary>
    /// Effect 4.0.6 flattens the special-number union into a wider union's own branches; the one
    /// plain number branch and its special-spelling siblings fold back into the single special-number
    /// branch the nested form projected. Only adjacent members fold, as the flattening leaves them, so
    /// the fold never moves the special spellings ahead of a branch that first-match gives them to.
    /// </summary>
    public static int[]? FindFlat(IList<IOpenApiSchema> branches)
    {
        var numbers = Enumerable.Range(0, branches.Count).Where(index => branches[index] is OpenApiSchema { Type: JsonSchemaType.Number, Enum: null, Const: null } number
            && !UnionBranchShape.HasCompetingNodeConstraints(number)).ToArray();
        var specials = Enumerable.Range(0, branches.Count).Where(index => branches[index] is OpenApiSchema special && IsSpecialStringBranch(special)).ToArray();
        int[] members = [.. numbers.Concat(specials).Order()];
        return numbers.Length is 1 && specials.Length > 0 && members[^1] - members[0] == members.Length - 1 ? members : null;
    }

    private static bool CountSpecialNumberBranches(IEnumerable<IOpenApiSchema> branches, bool allowNested, ref int numberBranches,
        ref int specialBranches)
    {
        foreach (var branch in branches)
        {
            if (branch is not OpenApiSchema schema)
            {
                return false;
            }

            if (schema is { Type: JsonSchemaType.Number, Enum: null, Const: null } && !UnionBranchShape.HasCompetingNodeConstraints(schema))
            {
                numberBranches++;
                continue;
            }

            // The v2 dialect nests the special-number union one level deeper
            // (anyOf: [anyOf of number-and-specials, a redundant specials enum]); exactly one
            // nesting level flattens, anything deeper or impure stays refused.
            if (allowNested && UnionBranchShape.IsPureAnyOfBranch(schema))
            {
                if (!CountSpecialNumberBranches(schema.AnyOf!, allowNested: false, ref numberBranches, ref specialBranches))
                {
                    return false;
                }

                continue;
            }

            if (!IsSpecialStringBranch(schema))
            {
                return false;
            }

            specialBranches++;
        }

        return true;
    }

    private static bool IsSpecialStringBranch(OpenApiSchema schema)
    {
        if (schema.Type is not JsonSchemaType.String || UnionBranchShape.HasCompetingNodeConstraints(schema))
        {
            return false;
        }

        if (schema is { Const: not null, Enum: null })
        {
            return SpecialNumberValues.Contains(schema.Const);
        }

        if (schema is not { Const: null, Enum.Count: > 0 } || schema.Enum is not { } values)
        {
            return false;
        }

        return values.All(static value => value is JsonValue jsonValue
                                          && jsonValue.TryGetValue<string>(out var text)
                                          && SpecialNumberValues.Contains(text));
    }
}
