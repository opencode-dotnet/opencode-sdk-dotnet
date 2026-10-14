using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>
/// What the server granted when it attached a connection to a persistent terminal. Knowledge
/// source: upstream-observed — the handler sends exactly one <c>attached</c> frame before any
/// replay, and everything a session needs to speak the wire correctly (the negotiated input
/// protocol, the granted role, the viewport, the replay anchor) arrives only there.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed record PersistentPtyAttachment
{
    /// <summary>
    /// Gets the identity this connection attached under: the one the request asked for, or the
    /// one the server minted when it asked for none.
    /// </summary>
    public required string AttachmentId { get; init; }

    /// <summary>
    /// Gets the input protocol the server negotiated. <c>1</c> is the framed protocol this SDK
    /// speaks; <c>0</c> means the server would treat every message as raw input with the viewport
    /// frozen at attach time, which <see cref="PersistentPtySession"/> refuses rather than sends
    /// input into.
    /// </summary>
    public required int InputProtocol { get; init; }

    /// <summary>Gets the terminal as it stood at attach time, including its viewport and retained output range.</summary>
    public required PersistentPtyInfo Info { get; init; }

    /// <summary>Gets the role the server granted, which is not necessarily the role the request asked for.</summary>
    public required PersistentPtyRole Role { get; init; }

    /// <summary>
    /// Gets the terminal's resize generation at attach time. It advances on every resize, so a
    /// caller replaying a checkpoint can tell whether the screen it holds is still current.
    /// </summary>
    public required long Generation { get; init; }

    /// <summary>Gets what the server replayed against what the connection asked for.</summary>
    public required PersistentPtyReplayBounds Replay { get; init; }
}
