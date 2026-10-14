namespace OpenCode.Sdk.Internal;

/// <summary>A finite connection send budget representable by every supported cancellation timer.</summary>
internal sealed record TerminalSendTimeout
{
    public TerminalSendTimeout(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, TimeSpan.FromMilliseconds(int.MaxValue));
        Value = value;
    }

    /// <summary>Gets the validated duration.</summary>
    public TimeSpan Value { get; }

    /// <summary>Gets the shared immutable default budget.</summary>
    public static TerminalSendTimeout Default { get; } = new(TerminalSocketBounds.DefaultSendTimeout);
}
