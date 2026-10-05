using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// How the wait for a server's readiness line ended: the line arrived, the server exited first,
/// or the server closed its standard output first while it kept running. Only the line is a start;
/// the other two are failures, and the child behind them has already been ended.
/// </summary>
internal sealed record ReadinessOutcome
{
    private ReadinessOutcome()
    {
    }

    /// <summary>Gets the first stdout line, when it arrived.</summary>
    public string? ReadyLine { get; private init; }

    /// <summary>Gets the server's exit, when it exited before the line.</summary>
    public ChildExitStatus? Exit { get; private init; }

    /// <summary>Gets the outcome of a server that closed its standard output, alive, before the line.</summary>
    public static ReadinessOutcome OutputClosed { get; } = new();

    /// <summary>Creates the outcome of an arrived line.</summary>
    /// <param name="line">The first stdout line.</param>
    /// <returns>The outcome.</returns>
    public static ReadinessOutcome Ready(string line) => new() { ReadyLine = line };

    /// <summary>Creates the outcome of a server that exited before the line.</summary>
    /// <param name="exit">How it exited.</param>
    /// <returns>The outcome.</returns>
    public static ReadinessOutcome Exited(ChildExitStatus exit) => new() { Exit = exit };
}
