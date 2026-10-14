namespace OpenCode.Sdk.Internal;

/// <summary>
/// Knowledge source: upstream-observed — both PTY families' connect-token handlers require the
/// <c>x-opencode-ticket</c> header to carry exactly this value; it exists only in upstream
/// implementation source (ADR-0013/0021), so it lives here in hand-written runtime code and never
/// in curation or generated output.
/// </summary>
internal static class PtyTicketHeader
{
    /// <summary>The only value the connect-token handlers accept in the ticket header.</summary>
    public const string Sentinel = "1";
}
