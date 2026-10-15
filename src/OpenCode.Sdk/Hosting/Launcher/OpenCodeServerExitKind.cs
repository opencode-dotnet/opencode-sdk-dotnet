namespace OpenCode.Sdk;

/// <summary>
/// How an owned server ended, as <see cref="OpenCodeServerExit.Kind"/> reports it. Members may be
/// added in a minor version, so a switch over this enum keeps a default arm.
/// </summary>
public enum OpenCodeServerExitKind
{
    /// <summary>
    /// The exit status could not be read: for example, on Linux and macOS something other than the
    /// launcher reaped the server (a host that ignores <c>SIGCHLD</c>, or a runtime running as pid 1
    /// that reaps every child), or the exit code could not be queried.
    /// </summary>
    Unknown = 0,

    /// <summary>The server exited with a code, <see cref="OpenCodeServerExit.ExitCode"/>.</summary>
    Exited = 1,

    /// <summary>A signal ended the server, <see cref="OpenCodeServerExit.Signal"/>; never on Windows.</summary>
    Signaled = 2,
}
