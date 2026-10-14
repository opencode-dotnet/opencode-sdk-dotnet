using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// A control frame whose <c>type</c> this SDK does not know. The persistent terminal socket is an
/// experimental surface: a server newer than the pinned document may send a kind that did not
/// exist when this SDK was built, and dropping the connection over it would be worse than
/// carrying it. The raw body rides along so a caller can read a kind the SDK cannot name yet.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyUnknownFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes an unknown frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="type">The control type the server sent; never null.</param>
    /// <param name="payload">The frame's body, detached from the document it was parsed from.</param>
    public PersistentPtyUnknownFrame(string type, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(type);

        Type = type;
        Payload = payload;
    }

    /// <summary>Gets the control type the server sent.</summary>
    public string Type { get; }

    /// <summary>Gets the frame's body; it outlives the parse, so a caller may keep it.</summary>
    public JsonElement Payload { get; }
}
