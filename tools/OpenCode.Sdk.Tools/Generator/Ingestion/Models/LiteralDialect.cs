namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies the wire spelling used to express a literal schema.</summary>
internal enum LiteralDialect
{
    /// <summary>The literal is expressed as an enum with exactly one value.</summary>
    SingleValueEnum = 0,

    /// <summary>The literal is expressed with the JSON Schema <c>const</c> keyword.</summary>
    Const = 1,
}
