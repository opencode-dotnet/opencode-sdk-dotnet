using Microsoft.JSInterop;

namespace OpenCode.Playground.Hq.Components;

/// <summary>Every screen's header: the connection and a way to start a session.</summary>
public sealed partial class MainLayout : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        Board.Changed -= OnChanged;
        Panel.Changed -= OnPanelChanged;
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Board.Changed += OnChanged;
        Panel.Changed += OnPanelChanged;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            var stored = await JS.InvokeAsync<string?>("localStorage.getItem", "hq.sdk-calls.open");
            if (stored == "true" && !Panel.Open)
            {
                Panel.Toggle();
            }
        }
        catch (JSException exception)
        {
            Logger.LogWarning(exception, "Could not read the SDK calls dock preference.");
        }
    }

    private void OnPanelChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work and reports render faults; storage failures are logged here.
        _ = InvokeAsync(async () =>
        {
            StateHasChanged();
            try
            {
                await JS.InvokeVoidAsync("localStorage.setItem", "hq.sdk-calls.open", Panel.Open ? "true" : "false");
            }
            catch (JSException exception)
            {
                Logger.LogWarning(exception, "Could not remember the SDK calls dock preference.");
            }
        });
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        // The renderer owns this work: a render failure reaches its own error handling, which logs it.
        _ = InvokeAsync(StateHasChanged);
    }
}
