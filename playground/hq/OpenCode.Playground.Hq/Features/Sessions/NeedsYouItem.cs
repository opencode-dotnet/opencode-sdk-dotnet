using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Sessions;

/// <summary>One ask waiting for a person.</summary>
/// <param name="Id">The request or form id.</param>
/// <param name="SessionId">The session that asks.</param>
/// <param name="Title">What it asks.</param>
/// <param name="IsForm">Whether this is a form rather than a permission.</param>
internal sealed record NeedsYouItem(string Id, string SessionId, string Title, bool IsForm)
{
    public static NeedsYouItem FromPermission(PermissionRequest ask) =>
        new(ask.Id, ask.SessionId, $"{ask.Action}: {string.Join(", ", ask.Resources)}", IsForm: false);

    public static NeedsYouItem FromAsked(PermissionAskedData ask) =>
        new(ask.Id, ask.SessionId, $"{ask.Action}: {string.Join(", ", ask.Resources)}", IsForm: false);

    public static NeedsYouItem FromForm(FormInfo form) => new(form.Id, form.SessionId, form.Title, IsForm: true);
}
