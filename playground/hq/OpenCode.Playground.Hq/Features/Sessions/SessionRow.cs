using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Sessions;

/// <summary>One line of the sessions list.</summary>
/// <param name="Session">The stored session.</param>
/// <param name="Status">Its live status.</param>
internal sealed record SessionRow(SessionInfo Session, string Status);
