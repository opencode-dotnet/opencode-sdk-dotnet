using System.Buffers;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Tracks byte-array ownership and pre-fills spare capacity so tests catch out-of-range reads.</summary>
internal sealed class TrackingByteArrayPool(int spareCapacity = 0) : ArrayPool<byte>
{
    private readonly Lock _gate = new();
    private readonly HashSet<byte[]> _outstanding = [];
    private readonly List<int> _requestedLengths = [];

    public int OutstandingCount
    {
        get
        {
            lock (_gate)
            {
                return _outstanding.Count;
            }
        }
    }

    public int RentCount { get; private set; }

    public int ReturnCount { get; private set; }

    public IReadOnlyList<int> RequestedLengths
    {
        get
        {
            lock (_gate)
            {
                return [.. _requestedLengths];
            }
        }
    }

    public override byte[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        lock (_gate)
        {
            var buffer = new byte[Math.Max(minimumLength, 64) + spareCapacity];
            for (var index = 0; index < buffer.Length; index++)
            {
                buffer[index] = 0xff;
            }

            _ = _outstanding.Add(buffer);
            _requestedLengths.Add(minimumLength);
            RentCount++;
            return buffer;
        }
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(array);
        lock (_gate)
        {
            if (!_outstanding.Remove(array))
            {
                throw new InvalidOperationException("The array was returned more than once or did not come from this pool.");
            }

            if (clearArray)
            {
                Array.Clear(array, 0, array.Length);
            }

            ReturnCount++;
        }
    }
}
