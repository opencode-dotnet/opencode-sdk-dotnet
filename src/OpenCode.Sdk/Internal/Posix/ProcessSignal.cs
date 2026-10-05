namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// The two rungs of the pinned client's <c>terminate</c>: a request to stop that the process may
/// handle, then an end it cannot refuse. On Unix they are <c>SIGTERM</c> and <c>SIGKILL</c>; on
/// Windows, where another process cannot be signalled, both are <c>TerminateProcess</c>, which is
/// what the pinned client's runtime does with a <c>SIGTERM</c> there as well.
/// </summary>
internal enum ProcessSignal
{
    /// <summary>The first rung: <c>SIGTERM</c>, a hard kill on Windows.</summary>
    Terminate,

    /// <summary>The second rung: <c>SIGKILL</c>, a hard kill on Windows.</summary>
    Kill,
}
