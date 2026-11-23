namespace OpenCode.Playground.Hq.Features.Sessions;

/// <summary>The first screen: what needs a person, then every session.</summary>
public sealed partial class SessionsScreen : IDisposable
{
    internal static string FolderName(string directory)
    {
        var path = directory.TrimEnd('\\', '/');
        return path[(Math.Max(path.LastIndexOf('\\', StringComparison.Ordinal), path.LastIndexOf('/', StringComparison.Ordinal)) + 1)..];
    }
    /// <inheritdoc />
    public void Dispose()
    {
        Board.Changed -= OnChanged;
    }

    /// <inheritdoc />
    protected override void OnInitialized() => Board.Changed += OnChanged;

    private void OnChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work: a render failure reaches its own error handling, which logs it.
        _ = InvokeAsync(StateHasChanged);
    }

}
