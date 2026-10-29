using System.Buffers;

namespace OpenCode.Sdk.Tests.Serialization.Support;

/// <summary>A buffer segment that splits one payload into a two-segment sequence at a chosen byte.</summary>
internal sealed class BufferSegment : ReadOnlySequenceSegment<byte>
{
    private BufferSegment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    public static ReadOnlySequence<byte> Split(byte[] payload, int splitAt)
    {
        var first = new BufferSegment(payload.AsMemory(0, splitAt), 0);
        var second = new BufferSegment(payload.AsMemory(splitAt), splitAt);
        first.Next = second;
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }
}
