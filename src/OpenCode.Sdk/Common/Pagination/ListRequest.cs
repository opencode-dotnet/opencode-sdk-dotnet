namespace OpenCode.Sdk;

/// <summary>
/// The cursor-pagination trio shared by list operations whose wire parameters match it
/// exactly; generated per-operation request records derive from this seam.
/// </summary>
public abstract record ListRequest
{
    /// <summary>Gets the wire limit value; the server default applies when unset.</summary>
    public string? Limit { get; init; }

    /// <summary>Gets the first-page order; the server default applies when unset.</summary>
    public ListOrder? Order { get; init; }

    /// <summary>Gets the opaque pagination cursor returned by a previous page.</summary>
    public string? Cursor { get; init; }
}
