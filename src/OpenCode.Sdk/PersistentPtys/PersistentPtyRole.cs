using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// What a connection may do with a persistent terminal. Knowledge source: upstream-observed — the
/// server grants the role, and the granted role rides the <c>attached</c> frame rather than the
/// request. A controller attach without takeover fails while another attachment controls the
/// terminal, and input sent on an observer's connection makes that attachment the controller.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public enum PersistentPtyRole
{
    /// <summary>The connection may write input and resize the terminal.</summary>
    Controller,

    /// <summary>The connection was attached to read output; input it sends makes it the controller.</summary>
    Observer,
}
