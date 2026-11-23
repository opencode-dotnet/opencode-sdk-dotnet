namespace OpenCode.Playground.Hq.Features.Conversation;

internal sealed class PromptDraft
{
    private readonly Dictionary<string, string> _drafts = [with(StringComparer.Ordinal)];
    public void Set(string sessionId, string text) => _drafts[sessionId] = text;
    public string? Take(string sessionId) => _drafts.Remove(sessionId, out var text) ? text : null;
}
