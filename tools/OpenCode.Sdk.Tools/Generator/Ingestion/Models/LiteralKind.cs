namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies the JSON primitive kind carried by a literal schema.</summary>
internal enum LiteralKind
{
    /// <summary>A JSON string literal.</summary>
    String = 0,

    /// <summary>A JSON boolean literal.</summary>
    Boolean = 1,

    /// <summary>A JSON number literal.</summary>
    Number = 2,
}
