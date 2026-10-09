namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// The bounds of the Windows ladder's last rung, which the grace does not cover: how long the
/// second tree kill may run before it is ended, and how long the ladder then waits for the root.
/// </summary>
/// <param name="TreeKill">How long the second tree kill may run.</param>
/// <param name="ForcedExit">How long the ladder waits for the root after it.</param>
internal sealed record WindowsLadderBounds(TimeSpan TreeKill, TimeSpan ForcedExit)
{
    /// <summary>Gets the shipped bounds: ten seconds each, so a disposal takes at most the grace plus twenty seconds.</summary>
    public static WindowsLadderBounds Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
}
