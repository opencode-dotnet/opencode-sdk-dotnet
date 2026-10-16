namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Identifies a recognized response-envelope property shape.</summary>
internal enum SpecEnvelopeShape
{
    /// <summary>The response has no content.</summary>
    None = 0,

    /// <summary>The response has content without a recognized envelope.</summary>
    Bare = 1,

    /// <summary>The response has exactly a <c>data</c> property.</summary>
    Data = 2,

    /// <summary>The response has exactly <c>data</c> and <c>location</c> properties.</summary>
    DataLocation = 3,

    /// <summary>The response has exactly <c>cursor</c> and <c>data</c> properties.</summary>
    CursorData = 4,

    /// <summary>The response has exactly <c>data</c> and <c>hasMore</c> properties.</summary>
    DataHasMore = 5,

    /// <summary>
    /// The operation declares its body inline as an object with exactly one property, named
    /// something other than <c>data</c> — envelope spine wearing its payload's own key.
    /// </summary>
    SingleKey = 6,
}
