namespace OpenCode.Sdk.Internal;

/// <summary>Creates the connection's per-operation cancellation timers.</summary>
internal interface ITerminalDeadlineFactory
{
    /// <summary>Starts one fixed deadline; the operation owns the returned source.</summary>
    /// <param name="timeout">The duration until cancellation.</param>
    /// <returns>The operation-owned cancellation source.</returns>
    public CancellationTokenSource Create(TimeSpan timeout);
}
