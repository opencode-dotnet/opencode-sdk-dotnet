namespace OpenCode.Playground.Hq.Features.Conversation;

/// <summary>One conversation line, with its author and text so far.</summary>
internal sealed record ConversationLine(string Author, string Text);
