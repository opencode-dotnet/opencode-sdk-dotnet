using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class EventDiagnosticScenario : ExperimentalClient
{
    public Exception? Failure { get; init; }

    public bool IncludeMarker { get; init; }

    public bool Stall { get; init; }

    public TaskCompletionSource<bool>? Pending { get; private set; }

    public TaskCompletionSource<bool> Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async IAsyncEnumerable<ISessionLogItem> ReadSessionLogAsync(
        string sessionId, ExperimentalSessionLogRequest? request = null, OpenCodeRequestOptions? requestOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (IncludeMarker)
        {
            yield return new EventLogSynced { AggregateId = "ses_owned", Seq = 1 };
        }

        foreach (var item in Observations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }

    public async IAsyncEnumerable<IEvent> EventsAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending = completion;
        foreach (var item in Observations())
        {
            yield return item;
        }

        if (Stall)
        {
            _ = Stalled.TrySetResult(true);
            _ = await completion.Task;
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }

    private static IEnumerable<Observation> Observations() => Enumerable.Range(0, 100)
        .Select(index => new Observation("event-" + index.ToString("D4", CultureInfo.InvariantCulture)
                                        + "-" + new string('x', 4000)));

    /// <summary>
    /// A stand-in event: the diagnostic under test reads only the marker, so the members the
    /// live event union hoists are answered explicitly and stay out of the probe's own surface.
    /// </summary>
    private sealed record Observation(string Type) : ISessionLogItem, IEvent
    {
        string? IEvent.Id => null;

        IReadOnlyDictionary<string, JsonElement>? IEvent.Metadata => null;
    }
}
