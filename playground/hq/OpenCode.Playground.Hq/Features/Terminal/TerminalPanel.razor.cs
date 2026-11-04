using Microsoft.AspNetCore.Components;

namespace OpenCode.Playground.Hq.Features.Terminal;

/// <summary>A session page's terminal; leaving the page removes it from the server.</summary>
public sealed partial class TerminalPanel : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private string _line = string.Empty;

    /// <summary>Gets or sets the session whose folder the terminal opens in.</summary>
    [Parameter]
    public string SessionId { get; set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Terminal.Changed -= OnChanged;
        await _lifetime.CancelAsync();
        await Terminal.CloseAsync();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    protected override void OnInitialized() => Terminal.Changed += OnChanged;

    private Task OpenAsync() => Terminal.OpenAsync(SessionId, _lifetime.Token);

    private Task SendAsync()
    {
        var line = _line;
        _line = string.Empty;
        return Terminal.SendAsync(line, _lifetime.Token);
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work: a render failure reaches its own error handling, which logs it.
        _ = InvokeAsync(StateHasChanged);
    }
}
