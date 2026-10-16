namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies a scalar primitive supported by schema projection.</summary>
internal enum PrimitiveKind
{
    /// <summary>A JSON string.</summary>
    String = 0,

    /// <summary>A JSON number.</summary>
    Number = 1,

    /// <summary>A JSON integer.</summary>
    Integer = 2,

    /// <summary>A JSON boolean.</summary>
    Boolean = 3,
}
