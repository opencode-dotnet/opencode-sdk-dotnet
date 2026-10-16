using System.Collections.Concurrent;
using System.Globalization;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// One FIFO of id-less JSON-RPC notifications of a single method, with a bounded dequeue. The
/// controller keeps one per notification kind so a model request, a tool invocation, and a tool
/// cancellation can never be mistaken for one another, and every wait names what it waited for.
/// </summary>
internal sealed class DriveNotificationQueue<T>(string method) : IDisposable
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly SemaphoreSlim _signal = new(0);

    public void Enqueue(T item)
    {
        _items.Enqueue(item);
        _ = _signal.Release();
    }

    /// <summary>Waits for the next notification inside the bound; <paramref name="describeLoopState"/> is quoted on a timeout.</summary>
    public async Task<T> DequeueAsync(TimeSpan timeout, Func<string> describeLoopState, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(describeLoopState);

        if (!await _signal.WaitAsync(timeout, lifetime))
        {
            throw new TimeoutException(
                $"No {method} arrived within {timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.{describeLoopState()}");
        }

        return _items.TryDequeue(out var item)
            ? item
            : throw new InvalidOperationException($"The {method} signal fired without a queued notification.");
    }

    public void Dispose() => _signal.Dispose();
}
