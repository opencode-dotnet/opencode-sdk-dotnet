namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies how an object schema treats properties not declared by name.</summary>
internal enum AdditionalPropertiesKind
{
    /// <summary>Additional properties are permitted without a schema constraint.</summary>
    Open = 0,

    /// <summary>Additional properties are forbidden.</summary>
    Forbidden = 1,

    /// <summary>Additional properties are constrained by a schema.</summary>
    Schema = 2,
}
