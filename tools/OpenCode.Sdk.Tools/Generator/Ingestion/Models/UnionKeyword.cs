namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies the OpenAPI keyword that declared a projected union.</summary>
internal enum UnionKeyword
{
    /// <summary>The union was declared with <c>anyOf</c>.</summary>
    AnyOf = 0,

    /// <summary>The union was declared with <c>oneOf</c>.</summary>
    OneOf = 1,
}
