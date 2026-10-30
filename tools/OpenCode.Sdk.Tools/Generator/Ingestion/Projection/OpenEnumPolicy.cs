using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>
/// Recognizes an open enum, a string enum beside a free string, and the one enum it is: the
/// generated extensible enum carries the enum's values and any other string.
/// </summary>
internal static class OpenEnumPolicy
{
    /// <summary>
    /// A string enum beside a free string is an open enum: their adjacent branches fold into one enum
    /// of the enum's values, which the generated extensible enum carries along with any other string.
    /// </summary>
    public static int[]? Find(IList<IOpenApiSchema> branches, int[]? specialNumber)
    {
        ArgumentNullException.ThrowIfNull(branches);

        int[] members = [.. Enumerable.Range(0, branches.Count).Where(index => specialNumber?.Contains(index) != true
            && Resolve(branches[index]) is OpenApiSchema { Type: JsonSchemaType.String, Format: null } text && !UnionBranchShape.HasCompetingNodeConstraints(text)
            && !IsNullSpelling(text)
            && (text.Enum ?? []).All(static value => value is JsonValue json && json.TryGetValue<string>(out _)))];
        var closed = members.Count(index => Resolve(branches[index]) is OpenApiSchema { Enum.Count: > 0 } or OpenApiSchema { Const: not null });
        return closed > 0 && closed < members.Length && members[^1] - members[0] == members.Length - 1 ? members : null;
    }

    /// <summary>A union that is nothing but an open enum, optionally nullable, is that enum.</summary>
    public static SchemaNode? ProjectWhole(OpenApiSchema schema, IList<IOpenApiSchema> branches)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(branches);

        var members = Find(branches, SpecialNumberPolicy.FindFlat(branches));
        if (members is null || branches.Count(static branch => branch is not OpenApiSchema { Type: JsonSchemaType.Null }) != members.Length)
        {
            return null;
        }

        var enumNode = new EnumNode { Description = schema.Description, Values = Values(branches, members) };
        return members.Length == branches.Count ? enumNode : new NullableNode { Description = schema.Description, Inner = enumNode };
    }

    /// <summary>The members of an open enum inside a wider union fold into one enum that keeps the first closed member's description.</summary>
    public static EnumNode Project(IList<IOpenApiSchema> branches, int[] members)
    {
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(members);

        return new EnumNode
        {
            Description = members.Select(index => (OpenApiSchema)Resolve(branches[index])!).First(static branch => branch.Enum is { Count: > 0 } || branch.Const is not null).Description,
            Values = Values(branches, members),
        };
    }

    private static string[] Values(IList<IOpenApiSchema> branches, int[] members) =>
    [
        .. members.Select(index => (OpenApiSchema)Resolve(branches[index])!)
            .SelectMany(static branch => branch.Const is { } constant ? [constant] : (branch.Enum ?? []).Select(static value => value.GetValue<string>()))
            .Distinct(StringComparer.Ordinal),
    ];

    private static IOpenApiSchema? Resolve(IOpenApiSchema branch) => branch is OpenApiSchemaReference reference ? reference.Target : branch;

    /// <summary>
    /// A lone "null" literal is upstream's spelling of null: beside a free string it is the spine's
    /// parent-session filter, whose value set binds the spine type, so it is never an open enum's value.
    /// </summary>
    private static bool IsNullSpelling(OpenApiSchema schema) =>
        schema.Const == "null" || (schema.Enum is [JsonValue only] && only.TryGetValue<string>(out var value) && value == "null");
}
