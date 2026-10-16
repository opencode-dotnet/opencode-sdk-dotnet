namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies how union branches can be distinguished.</summary>
internal enum UnionClassification
{
    /// <summary>Every branch is an object carrying a required literal or prefix marker.</summary>
    Marked = 0,

    /// <summary>The branches must be distinguished structurally.</summary>
    Structural = 1,
}
