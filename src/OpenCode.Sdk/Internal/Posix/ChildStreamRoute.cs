namespace OpenCode.Sdk.Internal.Posix;

/// <summary>Where one of a spawned child's standard descriptors (0, 1, or 2) leads.</summary>
internal enum ChildStreamRoute
{
    /// <summary><c>/dev/null</c>: the child reads end-of-file on standard input, and its writes to an output go nowhere.</summary>
    Null,

    /// <summary>A pipe whose other end the parent holds: it writes the child's standard input, or reads one of its outputs.</summary>
    Pipe,

    /// <summary>The parent's own descriptor of the same number, unchanged: the child writes where the parent does.</summary>
    Inherit,
}
