namespace OpenCode.Sdk.Internal;

/// <summary>What one whole-tree kill achieved, as far as the platform reports it.</summary>
internal enum ProcessTreeKillResult
{
    /// <summary>
    /// The kill was issued and the platform reported no failure. A kill is asynchronous, so this
    /// says nothing about whether every process has finished exiting yet.
    /// </summary>
    Issued,

    /// <summary>There was nothing left to end: the root had already exited, or its handle is gone.</summary>
    NothingToEnd,

    /// <summary>
    /// The kill ran but did not complete: some process of the tree could not be ended, the
    /// platform's kill reported a failure, or it did not finish inside its bound.
    /// </summary>
    Incomplete,
}
