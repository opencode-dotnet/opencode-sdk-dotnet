using Microsoft.OpenApi;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

/// <summary>The branch shapes a union's folding rules read: a pure nested union and a branch that carries competing constraints.</summary>
internal static class UnionBranchShape
{
    public static bool IsPureAnyOfBranch(OpenApiSchema schema) =>
        schema is { AnyOf.Count: > 0 } && schema.OneOf is not { Count: > 0 } && CarriesOnlyItsUnion(schema);

    /// <summary>The oneOf twin of <see cref="IsPureAnyOfBranch"/>; a nested marked union may use either keyword exclusively.</summary>
    public static bool IsPureOneOfBranch(OpenApiSchema schema) =>
        schema is { OneOf.Count: > 0 } && schema.AnyOf is not { Count: > 0 } && CarriesOnlyItsUnion(schema);

    public static bool HasCompetingNodeConstraints(OpenApiSchema schema) =>
        schema.AnyOf is { Count: > 0 }
        || schema.OneOf is { Count: > 0 }
        || schema.AllOf is { Count: > 0 }
        || schema.Required is { Count: > 0 }
        || schema.Items is not null
        || schema.Properties is not null
        || schema.PatternProperties is { Count: > 0 }
        || !schema.AdditionalPropertiesAllowed
        || schema.AdditionalProperties is not null
        || schema.ContentEncoding is not null
        || schema.ContentMediaType is not null
        || schema.ContentSchema is not null;

    private static bool CarriesOnlyItsUnion(OpenApiSchema schema) =>
        schema is { Type: null, Enum: null, Const: null, Items: null, Properties: null, AdditionalProperties: null }
        && schema.AllOf is not { Count: > 0 }
        && schema.Required is not { Count: > 0 }
        && schema.PatternProperties is not { Count: > 0 }
        && schema.AdditionalPropertiesAllowed
        && schema is { ContentEncoding: null, ContentMediaType: null, ContentSchema: null };
}
