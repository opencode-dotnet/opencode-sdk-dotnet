using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Sessions;

/// <summary>Session lifecycle controls shared by the board and conversation header.</summary>
public sealed partial class SessionActions : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private ElementReference _input;
    private string _draftTitle = "";
    private bool _renaming;
    private bool _focusRename;
    private bool _busy;
    private string Title => string.IsNullOrEmpty(Session.Title) ? "Untitled session" : Session.Title;
    private string DialogId => "delete-session-" + Session.Id;

    /// <summary>Gets or sets the session to act on.</summary>
    /// <remarks>Both screens supply Session before Blazor renders the component.</remarks>
    [Parameter]
    public SessionInfo Session { get; set; } = null!;

    /// <summary>Gets or sets the placement: row or header.</summary>
    [Parameter]
    public string Placement { get; set; } = "";

    /// <summary>Gets or sets the header's navigation after deletion succeeds.</summary>
    [Parameter]
    public EventCallback Deleted { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusRename)
        {
            _focusRename = false;
            await _input.FocusAsync();
        }
    }

    private void BeginRename()
    {
        _draftTitle = Session.Title ?? "";
        _renaming = true;
        _focusRename = true;
    }

    private void CancelRename() => _renaming = false;

    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            CancelRename();
        }
    }

    private async Task SaveAsync()
    {
        if (_busy)
        {
            return;
        }

        var title = _draftTitle.Trim();
        if (title.Length == 0 || title == Session.Title)
        {
            CancelRename();
            return;
        }

        _busy = true;
        try
        {
            await Board.RenameAsync(Session.Id, title, _lifetime.Token);
            if (Board.Find(Session.Id)?.Title == title)
            {
                CancelRename();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ArchiveAsync()
    {
        _busy = true;
        try
        {
            await Board.ArchiveAsync(Session.Id, _lifetime.Token);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteAsync()
    {
        _busy = true;
        try
        {
            if (await Board.DeleteAsync(Session.Id, _lifetime.Token))
            {
                await Deleted.InvokeAsync();
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
