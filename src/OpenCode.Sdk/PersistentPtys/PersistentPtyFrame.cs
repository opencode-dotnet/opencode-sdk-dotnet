using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// One message read from a live persistent PTY WebSocket. Knowledge source: upstream-observed —
/// binary messages are raw terminal output, and text messages are JSON control frames whose
/// <c>type</c> names one of seven kinds. The hierarchy is closed to this assembly but not to the
/// wire: an unrecognized control type arrives as <see cref="PersistentPtyUnknownFrame"/> rather
/// than failing the read, because the socket is an experimental surface that may grow kinds.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public abstract class PersistentPtyFrame
{
    private protected PersistentPtyFrame()
    {
    }
}
