using Markdig;
using Microsoft.AspNetCore.Components;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Conversation;

/// <summary>One session's conversation, asks, prompt box and changed files.</summary>
public sealed partial class SessionScreen : IDisposable
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
    internal static MarkupString Rendered(string text) => new(Markdown.ToHtml(text, Pipeline));
    private CancellationTokenSource _lifetime = new();
    private string _prompt = "";
    private string? _agent;
    private ModelRef? _model;
    private bool _forking;
    private string ModelChoice
    {
        get => _model is { } model ? $"{model.ProviderId}/{model.Id}" : "";
        set => _model = Conversation.Models.Keys.FirstOrDefault(model => $"{model.ProviderId}/{model.Id}" == value);
    }
    private bool _followLost;
    private bool _terminalTab;
    private int _changeCount;
    private string Title => Board.Find(SessionId)?.Title is { Length: > 0 } title ? title : "Untitled session";

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
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (Conversation.SessionId == SessionId)
        {
            return;
        }

        await _lifetime.CancelAsync();
        _lifetime.Dispose();
        _lifetime = new();
        _prompt = Draft.Take(SessionId) ?? "";
        _followLost = false;
        _changeCount = 0;
        // The screen owns this follow: it ends with _lifetime, and its faults reach FollowFaulted.
        _ = Conversation.FollowAsync(SessionId, _lifetime.Token).ContinueWith(
            FollowFaulted, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        // As upstream's session page does when it opens, the asks shown are the ones the server still lists.
        _ = Board.ResyncAsync(SessionId, _lifetime.Token).ContinueWith(
            ResyncFaulted, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await Conversation.LoadChoicesAsync(_lifetime.Token);
        _agent = Conversation.Agent;
        _model = Conversation.Model;
    }

    private void ResyncFaulted(Task resync) =>
        Logger.LogWarning(resync.Exception, "Could not list the asks of session {SessionId}.", SessionId);

    private void OnChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work: a render failure reaches its own error handling, which logs it.
        _ = InvokeAsync(() =>
        {
            // Choices the server lists late fill a picker that has no choice yet.
            _agent ??= Conversation.Agent;
            _model ??= Conversation.Model;
            StateHasChanged();
        });
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
        if (!await Conversation.SendAsync(text, _lifetime.Token, _agent, _model) && _prompt.Length == 0)
        {
            // As in upstream's app, a refused prompt goes back into the box, unless something else was typed there.
            _prompt = text;
        }
    }

    private async Task ForkAsync(ConversationLine line)
    {
        _forking = true;
        try
        {
            if (line.PromptId is { } messageId && await Conversation.ForkAsync(messageId, _lifetime.Token) is { } fork)
            {
                Board.Remember(fork);
                Draft.Set(fork.Id, line.Text);
                Navigation.NavigateTo($"/sessions/{fork.Id}");
            }
        }
        finally
        {
            _forking = false;
        }
    }

    private Task CompactAsync() => Conversation.CompactAsync(_model, _lifetime.Token);
    private Task StopAsync() => Conversation.StopAsync(_lifetime.Token);
    private void OnDeleted() => Navigation.NavigateTo("/");
}
