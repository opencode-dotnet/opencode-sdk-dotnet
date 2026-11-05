using Microsoft.AspNetCore.Components;
using OpenCode.Playground.Hq.Features.Sessions;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Conversation;

/// <summary>One session's conversation, asks, prompt box and changed files.</summary>
public sealed partial class SessionScreen : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private string _prompt = "";
    private bool _followLost;

    /// <summary>Gets or sets the session shown.</summary>
    [Parameter]
    public string SessionId { get; set; } = "";

    /// <inheritdoc />
    public void Dispose()
    {
        Conversation.Changed -= OnChanged;
        Board.Changed -= OnChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Conversation.Changed += OnChanged;
        Board.Changed += OnChanged;
        // The screen owns this follow: it ends with _lifetime, and its faults reach FollowFaulted.
        _ = Conversation.FollowAsync(SessionId, _lifetime.Token).ContinueWith(
            FollowFaulted, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work: a render failure reaches its own error handling, which logs it.
        _ = InvokeAsync(StateHasChanged);
    }

    private void FollowFaulted(Task follow)
    {
        Logger.LogWarning(follow.Exception, "Stopped following session {SessionId}; reload the page to follow it again.", SessionId);
        _followLost = true;
        OnChanged(this, EventArgs.Empty);
    }

    private async Task SendAsync()
    {
        var text = _prompt;
        _prompt = "";
        if (!await Conversation.SendAsync(text, _lifetime.Token) && _prompt.Length == 0)
        {
            // As in upstream's app, a refused prompt goes back into the box, unless something else was typed there.
            _prompt = text;
        }
    }

    private Task AnswerAsync(NeedsYouItem item, PermissionReply decision) => Board.AnswerAsync(item, decision, _lifetime.Token);
}
