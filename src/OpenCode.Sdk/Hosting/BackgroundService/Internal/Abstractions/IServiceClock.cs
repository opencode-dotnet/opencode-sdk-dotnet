namespace OpenCode.Sdk.Internal;

/// <summary>
/// The wall clock the Ensure election loop reads for its <c>PromiseTimeout</c> deadline, spawn
/// delay, and timeout counter. Behind a seam so tests drive the loop without waiting.
/// </summary>
internal interface IServiceClock
{
    /// <summary>Gets the current UTC time.</summary>
    public DateTimeOffset UtcNow { get; }
}
