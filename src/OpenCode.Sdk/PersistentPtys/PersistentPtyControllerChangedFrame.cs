using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// The terminal's controller changed. Knowledge source: upstream-observed — every attachment is
/// told, so a connection learns it lost control here rather than by noticing its input stopped
/// having an effect. A null attachment means the terminal is left with no controller at all.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyControllerChangedFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes a controller-changed frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="attachmentId">The attachment that now controls the terminal, or null when none does.</param>
    /// <param name="generation">The control generation this change belongs to.</param>
    public PersistentPtyControllerChangedFrame(string? attachmentId, long generation)
    {
        AttachmentId = attachmentId;
        Generation = generation;
    }

    /// <summary>
    /// Gets the attachment that now controls the terminal, or null when the terminal has no
    /// controller. Comparing it with <see cref="PersistentPtyAttachment.AttachmentId"/> is how a
    /// session tells whether it is the one in control.
    /// </summary>
    public string? AttachmentId { get; }

    /// <summary>Gets the control generation this change belongs to.</summary>
    public long Generation { get; }
}
