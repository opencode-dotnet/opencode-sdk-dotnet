namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>Identifies how a bound query parameter value maps between C# and the wire.</summary>
internal enum QueryValueKind
{
    /// <summary>A plain string carried verbatim.</summary>
    Text = 0,

    /// <summary>The asc/desc order enum exposed as the shared <c>ListOrder</c> spine type.</summary>
    ListOrder = 1,

    /// <summary>The exact true/false string enum exposed without converting it to a C# boolean.</summary>
    BooleanText = 2,

    /// <summary>The parent-session filter whose wire admits an identifier or the literal <c>"null"</c>.</summary>
    SessionParentFilter = 3,

    /// <summary>The deepObject location selector exposed as the shared <c>LocationSelector</c> spine type.</summary>
    Location = 4,

    /// <summary>A string enum outside the spine profiles, exposed as its own generated C# enum.</summary>
    Enum = 5,

    /// <summary>
    /// The value that fills a route's trailing wildcard: a required string escaped segment by
    /// segment into the path, never sent as a query parameter (ADR-0029).
    /// </summary>
    RouteTail = 6,

    /// <summary>
    /// A plain-string parameter upstream decodes as an integer, exposed as a 32-bit <c>int</c> and
    /// written as invariant query text (ADR-0037).
    /// </summary>
    Int32 = 7,

    /// <summary>
    /// A plain-string parameter upstream decodes as an integer that can pass the 32-bit range,
    /// exposed as a 64-bit <c>long</c> and written as invariant query text (ADR-0037).
    /// </summary>
    Int64 = 8,

    /// <summary>
    /// One generated enum or a list of it, exposed as a list and written as one key per element,
    /// as upstream's client writes an array into the query.
    /// </summary>
    EnumList = 9,
}
