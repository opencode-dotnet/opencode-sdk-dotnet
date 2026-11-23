using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Sessions;

/// <summary>One ask waiting for a person.</summary>
/// <param name="Id">The request or form id.</param>
/// <param name="SessionId">The session that asks.</param>
/// <param name="Title">What it asks.</param>
/// <param name="Form">The form, when this is one.</param>
public sealed record NeedsYouItem(string Id, string SessionId, string Title, FormInfo? Form = null)
{
    /// <summary>Gets the permission action as sent by the server.</summary>
    public string? Action { get; init; }
    /// <summary>Gets the resources awaiting approval.</summary>
    public IReadOnlyList<string> Resources { get; init; } = [];
    /// <summary>Gets the server's explanation.</summary>
    public string? Message { get; init; }
    /// <summary>Gets the tool input and any proposed edit metadata.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
    /// <summary>Gets proposed edits whose metadata includes both a file and a patch.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> PreviewFiles
    {
        get
        {
            if (Metadata?.TryGetValue("files", out var files) != true || files.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. files.EnumerateArray()
                .Where(static entry => entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.String
                    && entry.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.String)
                // The filter admits string properties only, so GetString cannot return null.
                .Select(static entry => KeyValuePair.Create(entry.GetProperty("file").GetString()!, entry.GetProperty("patch").GetString()!))];
        }
    }
    internal static string? KindFor(string action) => action switch
    {
        "read" => "Reading a file (matches the file path)",
        "edit" => "Modify files, including edits, writes, and patches",
        "glob" => "Match files using glob patterns",
        "grep" => "Search file contents using regular expressions",
        "list" => "List files within a directory",
        "bash" => "Run shell commands",
        "task" => "Launch sub-agents",
        "skill" => "Load a skill by name",
        "lsp" => "Run language server queries",
        "todowrite" => "Update the todo list",
        "webfetch" => "Fetch content from a URL",
        "websearch" => "Search the web",
        "external_directory" => "Access files outside the project directory",
        "doom_loop" => "Detect repeated tool calls with identical input",
        _ => null,
    };

    internal static NeedsYouItem FromPermission(PermissionRequest ask) =>
        new(ask.Id, ask.SessionId, $"{ask.Action}: {string.Join(", ", ask.Resources)}")
        {
            Action = ask.Action,
            Resources = ask.Resources,
            Message = ask.Message,
            Metadata = ask.Metadata,
        };

    internal static NeedsYouItem FromAsked(PermissionAskedData ask) =>
        new(ask.Id, ask.SessionId, $"{ask.Action}: {string.Join(", ", ask.Resources)}")
        {
            Action = ask.Action,
            Resources = ask.Resources,
            Message = ask.Message,
            Metadata = ask.Metadata,
        };

    internal static NeedsYouItem FromForm(FormInfo form) => new(form.Id, form.SessionId, form.Title, form);
}
