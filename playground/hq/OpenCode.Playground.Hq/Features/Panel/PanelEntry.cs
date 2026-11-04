namespace OpenCode.Playground.Hq.Features.Panel;

/// <summary>One item of the "How this screen works" panel.</summary>
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
    ];

    /// <summary>Gets the session screen's panel.</summary>
    public static IReadOnlyList<PanelEntry> SessionScreen { get; } =
    [
        new("History, then the live answer", "Features/Conversation/SessionConversation.cs", "follow"),
        new("Sending a prompt", "Features/Conversation/SessionConversation.cs", "prompt"),
        new("Changed files", "Features/Files/ChangedFiles.razor", "diff"),
        new("A terminal in the session's folder", "Features/Terminal/SessionTerminal.cs", "terminal"),
    ];
}
