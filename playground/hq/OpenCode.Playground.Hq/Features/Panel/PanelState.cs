namespace OpenCode.Playground.Hq.Features.Panel;

/// <summary>One circuit's choice to show the SDK calls dock.</summary>
internal sealed class PanelState
{
    public event EventHandler? Changed;
    public bool Open { get; private set; }

    public void Toggle()
    {
        Open = !Open;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
