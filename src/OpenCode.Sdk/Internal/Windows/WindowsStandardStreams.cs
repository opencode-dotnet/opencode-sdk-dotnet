using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// Where a Windows child's three standard handles lead. A pipe is created per stream, with the
/// parent's end returned and the child's end handed over; NUL is opened per stream; an inherited
/// stream is the host's own handle of the same number, passed on as it is, or no handle at all when
/// the host has none (a GUI host or a service).
/// </summary>
internal sealed record WindowsStandardStreams
{
    /// <summary>Gets where the child's standard input leads.</summary>
    public required ChildStreamRoute Input { get; init; }

    /// <summary>Gets where the child's standard output leads.</summary>
    public required ChildStreamRoute Output { get; init; }

    /// <summary>Gets where the child's standard error leads.</summary>
    public required ChildStreamRoute Error { get; init; }

    /// <summary>Gets a value indicating whether any of the three is the host's own handle.</summary>
    public bool InheritsAny =>
        Input is ChildStreamRoute.Inherit || Output is ChildStreamRoute.Inherit || Error is ChildStreamRoute.Inherit;
}
