using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Sessions;

/// <summary>The first screen: what needs a person, then every session.</summary>
public sealed partial class SessionsScreen : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    /// <inheritdoc />
    public void Dispose()
    {
        Board.Changed -= OnChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    protected override void OnInitialized() => Board.Changed += OnChanged;

    private void OnChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work: a render failure reaches its own error handling, which logs it.
        _ = InvokeAsync(StateHasChanged);
    }

    private Task AnswerAsync(NeedsYouItem item, PermissionReply decision) => Board.AnswerAsync(item, decision, _lifetime.Token);
}
