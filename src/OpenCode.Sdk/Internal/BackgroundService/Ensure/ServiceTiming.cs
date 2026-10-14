namespace OpenCode.Sdk.Internal;

/// <summary>
/// The pinned client's lifecycle timing (<c>defaultEnsureTiming</c> in <c>service-timing.ts</c>),
/// injectable and never public: discovery uses the request bound, Ensure and Stop the rest, and
/// the tests accelerate all of them the way upstream's own fixture does.
/// </summary>
/// <param name="RequestTimeout">The bound on one info request.</param>
/// <param name="PollInterval">The Ensure loop's spacing between probes; at the pin the loop is bounded by wall clock, not by an attempt count.</param>
/// <param name="SpawnDelay">The delay before a contender is started while a registration is unresolved.</param>
/// <param name="MaxSpawnDelay">The cap on the exit-0 backoff.</param>
/// <param name="PromiseTimeout">The Ensure loop's wall-clock bound, upstream's <c>Schedule.spaced(pollInterval).upTo(promiseTimeout)</c> repeat (<c>effect/service.ts</c>).</param>
/// <param name="StopPollInterval">The spacing between liveness polls after a stop signal.</param>
/// <param name="StopPollAttempts">The liveness polls per termination rung after the first look, so a rung waits about five seconds at the pin.</param>
internal sealed record ServiceTiming(
    TimeSpan RequestTimeout,
    TimeSpan PollInterval,
    TimeSpan SpawnDelay,
    TimeSpan MaxSpawnDelay,
    TimeSpan PromiseTimeout,
    TimeSpan StopPollInterval,
    int StopPollAttempts)
{
    /// <summary>Gets upstream's values at the pin: 2 s, 25 ms, 5 s, 30 s, 120 s, 50 ms, 100.</summary>
    public static ServiceTiming Default { get; } = new(
        RequestTimeout: TimeSpan.FromSeconds(2),
        PollInterval: TimeSpan.FromMilliseconds(25),
        SpawnDelay: TimeSpan.FromSeconds(5),
        MaxSpawnDelay: TimeSpan.FromSeconds(30),
        PromiseTimeout: TimeSpan.FromSeconds(120),
        StopPollInterval: TimeSpan.FromMilliseconds(50),
        StopPollAttempts: 100);
}
