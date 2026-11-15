namespace OpenCode.Sdk;

/// <summary>Chooses how a stop handles persistent terminals, matching the pinned client's <c>pty: "clear" | "handoff"</c>.</summary>
public enum OpenCodeServerStopPersistentPtyMode
{
    /// <summary>Removes any handoff ticket, so the persistent terminals end with the service.</summary>
    Clear,

    /// <summary>Attempts a best-effort handoff for the next service; preparation and adoption must precede the ticket's expiry.</summary>
    Handoff,
}
