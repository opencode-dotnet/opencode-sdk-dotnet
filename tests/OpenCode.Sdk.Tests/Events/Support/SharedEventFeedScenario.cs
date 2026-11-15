using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Events.Support;

/// <summary>Owns the feed and all attempts until their cleanup and observations have completed.</summary>
internal sealed class SharedEventFeedScenario : IAsyncDisposable
{
    public SharedEventFeedScenario(IEventFeedGateProbe? probe = null)
    {
        Feed = new SharedEventFeed(Attempts, Time, EventStreamSteps.IdleWindow, probe ?? new NoOpEventFeedGateProbe());
    }

    public ManualTimeProvider Time { get; } = new();
    public ScriptedEventAttemptSource Attempts { get; } = new();
    public SharedEventFeed Feed { get; }
    public ScriptedEventAttemptSource.Attempt? CurrentAttempt { get; private set; }

    public EventSubscription<IEvent> CreateSubscription(CancellationToken caller = default, CancellationToken enumeration = default) =>
        (EventSubscription<IEvent>)Feed.Subscribe(EventSelection.Capture(null), caller).GetAsyncEnumerator(enumeration);

    public EventStateWatcher CreateWatcher(CancellationToken caller = default, CancellationToken enumeration = default) =>
        (EventStateWatcher)Feed.Watch(caller).GetAsyncEnumerator(enumeration);

    public async Task<EventSubscription<IEvent>> AdmitAsync(string id, CancellationToken cancellationToken = default)
    {
        var opensSource = Feed.GetCurrentSource() is null;
        var events = CreateSubscription(cancellationToken);
        var moving = events.MoveNextAsync().AsTask();
        if (opensSource)
        {
            CurrentAttempt = await Attempts.NextOpenAsync();
            await CurrentAttempt.PublishAsync(Marker(id));
        }

        if (!await moving.WaitAsync(HangGuard.Bound, CancellationToken.None))
        {
            throw new InvalidOperationException("The subscription ended instead of being admitted.");
        }

        return events;
    }

    public async Task PublishAsync(string id) => await CurrentAttempt!.PublishAsync(Event(id));

    public async Task PublishPrefixAsync(params string[] ids)
    {
        foreach (var id in ids)
        {
            await PublishAsync(id);
        }
    }

    public async Task<EventConnectionState> FailAttemptAsync(ScriptedEventAttemptSource.Attempt attempt, Exception failure)
    {
        _ = attempt.Items.Writer.TryComplete(failure);
        attempt.ReleaseCleanup();
        await Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        return Feed.GetConnectionState();
    }

    public async Task FaultAsync(Exception failure)
    {
        CurrentAttempt!.CleanupFailure = failure;
        _ = CurrentAttempt.Items.Writer.TryComplete();
        CurrentAttempt.ReleaseCleanup();
        await Feed.JoinAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
    }

    public static EventServerConnected Marker(string id) => new()
    {
        Id = id,
        Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
    };

    public static EventRpc Event(string id) => new()
    {
        Id = id,
        Created = 0,
        Type = "rpc.sdk-test",
        Location = new LocationPublicRef { Directory = "/test" },
        Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            Feed.Dispose();
        }
        finally
        {
            Attempts.ReleaseCleanup();
            await Feed.JoinAsync().WaitAsync(HangGuard.Bound, CancellationToken.None);
        }
    }
}
