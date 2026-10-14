using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// The first frame the server sends: what it granted this connection. A session consumes exactly
/// that one during <see cref="PersistentPtyClient.ConnectAsync"/> and exposes it as
/// <see cref="PersistentPtySession.Attachment"/>. Only the first is consumed: an <c>attached</c>
/// the server sends later reaches the read enumeration like any other frame, and
/// <see cref="PersistentPtySession.Attachment"/> still reports the grant the connection opened
/// with.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyAttachedFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes an attached frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="attachment">What the server granted; never null.</param>
    public PersistentPtyAttachedFrame(PersistentPtyAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        Attachment = attachment;
    }

    /// <summary>Gets what the server granted at attach time.</summary>
    public PersistentPtyAttachment Attachment { get; }
}
