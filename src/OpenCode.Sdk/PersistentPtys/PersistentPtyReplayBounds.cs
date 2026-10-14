using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// What the server actually replayed, against what the connection asked for. Knowledge source:
/// upstream-observed — the retained buffer is trimmed as it grows, so a cursor pointing at bytes
/// the terminal no longer holds is silently advanced to the oldest retained byte;
/// <see cref="Truncated"/> is how a caller learns that output between the two offsets is gone.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed record PersistentPtyReplayBounds
{
    /// <summary>Initializes the bounds of one replay.</summary>
    /// <param name="requestedOffset">The output cursor the connect request asked to resume from.</param>
    /// <param name="availableOffset">The oldest retained output cursor the replay could start at.</param>
    /// <param name="endOffset">The output cursor the replay ends at; the anchor for a later resume.</param>
    /// <param name="truncated">Whether output the request asked for had already been trimmed.</param>
    public PersistentPtyReplayBounds(long requestedOffset, long availableOffset, long endOffset, bool truncated)
    {
        RequestedOffset = requestedOffset;
        AvailableOffset = availableOffset;
        EndOffset = endOffset;
        Truncated = truncated;
    }

    /// <summary>Gets the output cursor the connect request asked to resume from.</summary>
    public long RequestedOffset { get; init; }

    /// <summary>Gets the oldest retained output cursor the replay could start at.</summary>
    public long AvailableOffset { get; init; }

    /// <summary>Gets the output cursor the replay ends at; the anchor for a later resume.</summary>
    public long EndOffset { get; init; }

    /// <summary>Gets whether output the request asked for had already been trimmed.</summary>
    public bool Truncated { get; init; }

    /// <summary>Deconstructs the bounds in constructor order.</summary>
    /// <param name="requestedOffset">The output cursor the connect request asked to resume from.</param>
    /// <param name="availableOffset">The oldest retained output cursor the replay could start at.</param>
    /// <param name="endOffset">The output cursor the replay ends at.</param>
    /// <param name="truncated">Whether output the request asked for had already been trimmed.</param>
    public void Deconstruct(out long requestedOffset, out long availableOffset, out long endOffset, out bool truncated)
    {
        requestedOffset = RequestedOffset;
        availableOffset = AvailableOffset;
        endOffset = EndOffset;
        truncated = Truncated;
    }
}
