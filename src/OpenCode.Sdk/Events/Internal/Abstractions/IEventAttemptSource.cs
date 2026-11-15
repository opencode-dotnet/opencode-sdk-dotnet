using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>Opens one physical event attempt; the source runner owns its windows and retry.</summary>
internal interface IEventAttemptSource
{
    public IAsyncEnumerable<IEvent> OpenAsync(StreamAttempt attempt, CancellationToken cancellationToken);
}
