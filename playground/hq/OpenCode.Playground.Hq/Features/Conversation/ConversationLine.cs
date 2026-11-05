namespace OpenCode.Playground.Hq.Features.Conversation;

/// <summary>
/// One conversation line, with its author and text so far. A user line carries its message id, and a prompt HQ sent
/// is pending until the server takes it.
/// </summary>
internal sealed record ConversationLine(string Author, string Text, string? PromptId = null, bool Pending = false);
