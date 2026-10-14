using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// What a connection may do with a persistent terminal. Knowledge source: upstream-observed — the
/// server grants the role, so a connection that asked to control can still be attached as an
/// observer, and the granted role rides the <c>attached</c> frame rather than the request.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public enum PersistentPtyRole
{
    /// <summary>The connection may write input and resize the terminal.</summary>
    Controller,

    /// <summary>The connection reads output only; its input is ignored.</summary>
    Observer,
}
