namespace OpenCode.Sdk.TestSupport;

/// <summary>The credential variable names a session scrubs, and why they are incomplete when they are.</summary>
/// <param name="Names">The names to remove, compared without regard to case.</param>
/// <param name="Unavailable">
/// Null when the pinned checkout named them; otherwise why it could not, which every server start
/// in the session reports instead of starting.
/// </param>
internal sealed record ProviderCredentials(HashSet<string> Names, string? Unavailable);
