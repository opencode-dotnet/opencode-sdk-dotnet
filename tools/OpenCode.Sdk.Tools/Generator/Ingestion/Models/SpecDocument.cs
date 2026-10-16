namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>The ingested spec: the Binder's sole spec-side input.</summary>
internal sealed record SpecDocument
{
    /// <summary>Gets the projected operations in document order.</summary>
    public required IReadOnlyList<SpecOperation> Operations
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<SpecOperation>());

    /// <summary>Gets the schema graph under ordinal-sorted keys.</summary>
    public required IReadOnlyDictionary<string, SchemaNode> Schemas
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }
}
