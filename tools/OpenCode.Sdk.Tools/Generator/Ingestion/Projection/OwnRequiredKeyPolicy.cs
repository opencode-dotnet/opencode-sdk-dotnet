using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>
/// Keys an inline object branch by its first declared required key that no sibling branch requires,
/// as a literal marker keys its branch, so its name survives a reorder. A sibling behind a reference
/// counts with its target's required keys; a key that collides with another branch's key refuses as
/// a graph key collision.
/// </summary>
internal static class OwnRequiredKeyPolicy
{
    public static string? Find(IList<IOpenApiSchema> branches, int index)
    {
        ArgumentNullException.ThrowIfNull(branches);

        return branches[index] is OpenApiSchema { Type: JsonSchemaType.Object, Properties: not null, Required.Count: > 0 } branch
            ? branch.Properties.Keys.FirstOrDefault(key => branch.Required.Contains(key)
                && branches.Where((_, other) => other != index).All(sibling => !RequiredKeys(sibling).Contains(key)))
            : null;
    }

    private static ISet<string> RequiredKeys(IOpenApiSchema branch)
    {
        var candidate = branch is OpenApiSchemaReference reference ? reference.Target : branch;
        return candidate is OpenApiSchema { Required: { } required } ? required : new HashSet<string>(StringComparer.Ordinal);
    }
}
