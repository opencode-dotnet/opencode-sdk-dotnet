namespace OpenCode.Sdk.Internal;

/// <summary>Starts cancellation timers using the platform timer implementation.</summary>
internal sealed class TerminalDeadlineFactory : ITerminalDeadlineFactory
{
    private TerminalDeadlineFactory()
    {
    }

    /// <summary>Gets the shared timer factory.</summary>
    public static TerminalDeadlineFactory Instance { get; } = new();

    /// <inheritdoc />
    public CancellationTokenSource Create(TimeSpan timeout) => new(timeout);
}
