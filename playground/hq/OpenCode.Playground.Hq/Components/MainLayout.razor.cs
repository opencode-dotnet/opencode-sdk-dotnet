namespace OpenCode.Playground.Hq.Components;

/// <summary>Every screen's header: the connection and a way to start a session.</summary>
public sealed partial class MainLayout : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => Board.Changed -= OnChanged;

    /// <inheritdoc />
    protected override void OnInitialized() => Board.Changed += OnChanged;

    private void OnChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work: a render failure reaches its own error handling, which logs it.
        _ = InvokeAsync(StateHasChanged);
    }
}
