namespace OpenCode.Sdk;

/// <summary>
/// A stable copy of the output an <see cref="OpenCodeServerOutput"/> collector retained: each
/// stream's lines oldest first, and per stream whether any line or part of a line was
/// discarded to stay inside the retention bounds. Order holds within a stream; no ordering
/// between the two streams is promised.
/// </summary>
public sealed record OpenCodeServerOutputSnapshot
{
    /// <summary>Gets the retained stdout lines, the readiness line first when it was retained.</summary>
    public required IReadOnlyList<string> StandardOutput { get; init; }

    /// <summary>Gets the retained stderr lines.</summary>
    public required IReadOnlyList<string> StandardError { get; init; }

    /// <summary>Gets whether stdout lost any line or part of a line to the retention bounds.</summary>
    public required bool StandardOutputTruncated { get; init; }

    /// <summary>Gets whether stderr lost any line or part of a line to the retention bounds.</summary>
    public required bool StandardErrorTruncated { get; init; }
}
