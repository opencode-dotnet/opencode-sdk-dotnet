using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>One move's immutable result; all selection and settlement happen under the feed gate.</summary>
internal sealed class EventPendingMove<T>(TaskCompletionSource<Delivery<T>> completion) where T : class
{
    private readonly TaskCompletionSource<Delivery<T>> _completion = completion;

    public bool IsSettled => _completion.Task.IsCompleted;

    public bool Settle(Delivery<T> delivery)
    {
        var settled = _completion.TrySetResult(delivery);
        Debug.Assert(IsSettled, "A settlement must leave the move settled, including a refused repeat.");
        return settled;
    }
}
