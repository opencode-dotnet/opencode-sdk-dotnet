using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>
/// Keys an inline object branch by the one required key no sibling branch requires, as a literal
/// marker keys its branch, so its name survives a reorder.
/// </summary>
/// <remarks>Several such keys keep the ordinal, since a choice among them would move with property
/// order; a sibling behind a reference counts with its target's required keys; a key that collides
/// with another branch's key refuses as a schema graph key collision.</remarks>
internal static class OwnRequiredKeyPolicy
{
    public static string? Find(IList<IOpenApiSchema> branches, int index)
    {
        ArgumentNullException.ThrowIfNull(branches);

        if (branches[index] is not OpenApiSchema { Type: JsonSchemaType.Object, Properties: not null, Required.Count: > 0 } branch)
        {
            return null;
        }

        var own = branch.Properties.Keys.Where(key => branch.Required.Contains(key)
            && branches.Where((_, other) => other != index).All(sibling => !RequiredKeys(sibling).Contains(key))).Take(2).ToArray();
        return own is [var key] ? key : null;
    }

    private static ISet<string> RequiredKeys(IOpenApiSchema branch)
    {
        var candidate = branch is OpenApiSchemaReference reference ? reference.Target : branch;
        return candidate is OpenApiSchema { Required: { } required } ? required : new HashSet<string>(StringComparer.Ordinal);
    }
}
