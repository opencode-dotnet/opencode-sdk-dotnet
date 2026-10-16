namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>
/// Describes a required string property whose value is constrained to a literal prefix —
/// Effect's <c>TemplateLiteral([prefix, String])</c> projected as <c>^prefix[\s\S]*?$</c>.
/// </summary>
internal sealed record PrefixMarker
{
    /// <summary>Gets the opaque wire property name.</summary>
    public required string PropertyName
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the literal prefix with regular-expression escapes removed.</summary>
    public required string Prefix
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            field = value;
        }
    }
}
