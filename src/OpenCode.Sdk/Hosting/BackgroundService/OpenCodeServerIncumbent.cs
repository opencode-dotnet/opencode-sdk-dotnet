namespace OpenCode.Sdk;

/// <summary>
/// The registered background service <see cref="OpenCodeServer.FindIncumbentAsync"/> found at the
/// caller's address: a compatible service that already owns that address.
/// </summary>
public sealed class OpenCodeServerIncumbent
{
    internal OpenCodeServerIncumbent(OpenCodeServer server, OpenCodeServerState state)
    {
        Server = server;
        State = state;
    }

    /// <summary>
    /// Gets a non-owning handle at the caller's address, with the registration's password and
    /// process id. <see cref="OpenCodeServer.OwnsProcess"/> is false, disposal is a no-op, and
    /// <see cref="OpenCodeServer.CreateClient"/> works in every state.
    /// </summary>
    public OpenCodeServer Server { get; }

    /// <summary>Gets the state the service answered its info route with.</summary>
    public OpenCodeServerState State { get; }
}
