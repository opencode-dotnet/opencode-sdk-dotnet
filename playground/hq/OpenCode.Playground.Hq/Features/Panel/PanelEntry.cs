namespace OpenCode.Playground.Hq.Features.Panel;

/// <summary>One item of the SDK calls dock.</summary>
/// <param name="Title">What the code shows.</param>
/// <param name="File">The file, under the project.</param>
/// <param name="Region">The marked region in that file.</param>
public sealed record PanelEntry(string Title, string File, string Region)
{
    /// <summary>Gets the first screen's panel.</summary>
    public static IReadOnlyList<PanelEntry> FirstScreen { get; } =
    [
        new("Live status comes from one subscription", "Features/Sessions/SessionBoard.cs", "subscribe"),
        new("\"Needs you\" is a typed event", "Features/Sessions/SessionBoard.cs", "asks"),
        new("Answering a permission", "Features/Sessions/SessionBoard.cs", "reply"),
        new("Answering a question", "Features/Sessions/SessionBoard.cs", "form-reply"),
        new("Dismissing a question", "Features/Sessions/SessionBoard.cs", "form-dismiss"),
        new("Renaming a session", "Features/Sessions/SessionBoard.cs", "rename"),
        new("Archiving a session", "Features/Sessions/SessionBoard.cs", "archive"),
        new("Deleting a session", "Features/Sessions/SessionBoard.cs", "delete"),
    ];

    /// <summary>Gets the session screen's panel.</summary>
    public static IReadOnlyList<PanelEntry> SessionScreen { get; } =
    [
        new("History, then the live answer", "Features/Conversation/SessionConversation.cs", "follow"),
        new("Sending a prompt", "Features/Conversation/SessionConversation.cs", "prompt"),
        new("Stopping a turn", "Features/Conversation/SessionConversation.cs", "stop"),
        new("Renaming a session", "Features/Sessions/SessionBoard.cs", "rename"),
        new("Deleting a session", "Features/Sessions/SessionBoard.cs", "delete"),
        new("Answering a permission", "Features/Sessions/SessionBoard.cs", "reply"),
        new("Answering a question", "Features/Sessions/SessionBoard.cs", "form-reply"),
        new("Dismissing a question", "Features/Sessions/SessionBoard.cs", "form-dismiss"),
        new("Changed files", "Features/Files/ChangedFiles.razor", "diff"),
        new("A terminal in the session's folder", "Features/Terminal/SessionTerminal.cs", "terminal"),
    ];
}
