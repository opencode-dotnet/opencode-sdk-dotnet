namespace OpenCode.Sdk.Internal.Windows;

/// <summary>How the assignment of a server to the launcher's job ended, when it did not fail the start.</summary>
internal enum JobAssignment
{
    /// <summary>The server is in the job, and ends when the host does.</summary>
    Assigned,

    /// <summary>
    /// The kernel refused with <c>ERROR_ACCESS_DENIED</c>, which libuv tolerates: a job of the host's
    /// does not let the server nest. The server runs, and only its stdin lease ends it with its owner.
    /// </summary>
    Refused,
}
