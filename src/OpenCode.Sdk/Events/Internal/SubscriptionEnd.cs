namespace OpenCode.Sdk.Internal;

/// <summary>The first ending and its backlog effect; explicit enumerator disposal is tracked separately.</summary>
internal sealed record SubscriptionEnd(Exception? Error, bool PreservesBacklog, bool IsSubscriberEnding);
