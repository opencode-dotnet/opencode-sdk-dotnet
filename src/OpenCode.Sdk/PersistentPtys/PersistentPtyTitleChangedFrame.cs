using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// The terminal's title changed. Knowledge source: upstream-observed — the daemon reports the
/// title the shell set through its escape sequence, so this is what a tab or pane label shows.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyTitleChangedFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes a title-changed frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="title">The terminal's new title; never null.</param>
    public PersistentPtyTitleChangedFrame(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        Title = title;
    }

    /// <summary>Gets the terminal's new title.</summary>
    public string Title { get; }
}
