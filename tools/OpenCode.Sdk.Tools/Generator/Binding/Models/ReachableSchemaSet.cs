namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record ReachableSchemaSet
{
    public required IReadOnlyList<string> GraphKeys
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    public required IReadOnlyList<string> ResponseRootKeys
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>
    /// Gets graph keys a selected operation's request body reaches. A property of one of these
    /// schemas that the document declares both optional and nullable emits the tri-state
    /// <c>Optional&lt;T&gt;</c> wrapper; response-only schemas keep <c>T?</c> (ADR-0004).
    /// </summary>
    public IReadOnlyList<string> RequestReachableKeys
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Gets graph keys reached through an effect-stream cause schema.</summary>
    public IReadOnlyList<string> StreamCauseKeys
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());
}
