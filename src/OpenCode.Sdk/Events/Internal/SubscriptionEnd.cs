using System.Runtime.ExceptionServices;

namespace OpenCode.Sdk.Internal;

/// <summary>The first ending and its backlog effect; explicit enumerator disposal is tracked separately.</summary>
internal sealed record SubscriptionEnd(Exception? Error, bool PreservesBacklog, bool IsSubscriberEnding)
{
    public ExceptionDispatchInfo? Dispatch { get; } = Error is null ? null : ExceptionDispatchInfo.Capture(Error);
}
