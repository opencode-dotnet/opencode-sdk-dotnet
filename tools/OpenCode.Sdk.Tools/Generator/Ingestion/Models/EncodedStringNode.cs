namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>
/// Represents a projected string schema carrying a declared content encoding. The node is a
/// distinct kind rather than a primitive so every plain-string expectation refuses it instead
/// of silently matching; its binding representation is a deliberate later decision.
/// </summary>
internal sealed record EncodedStringNode : SchemaNode
{
    /// <summary>Gets the declared content encoding, exactly as the wire schema states it.</summary>
    public required string ContentEncoding
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
