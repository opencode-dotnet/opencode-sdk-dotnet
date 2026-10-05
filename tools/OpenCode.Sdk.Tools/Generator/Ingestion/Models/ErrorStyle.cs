namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies an object schema's recognized error payload convention.</summary>
public enum ErrorStyle
{
    /// <summary>The object is not a recognized error payload.</summary>
    None = 0,

    /// <summary>The object uses a required literal <c>_tag</c> property.</summary>
    EffectTag = 1,
}
