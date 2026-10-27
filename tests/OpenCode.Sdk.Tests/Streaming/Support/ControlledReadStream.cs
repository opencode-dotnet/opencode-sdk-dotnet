namespace OpenCode.Sdk.Tests.Streaming.Support;

/// <summary>A read-only stream whose asynchronous reads run the test's controlled action.</summary>
internal sealed class ControlledReadStream : MemoryStream
{
    private readonly Func<Memory<byte>, CancellationToken, Task<int>> _read;

    public ControlledReadStream(Func<Memory<byte>, CancellationToken, Task<int>> read)
        : base([], writable: false)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _read(buffer.AsMemory(offset, count), cancellationToken);

#if NET
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(_read(buffer, cancellationToken));
#endif

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The controlled stream is read asynchronously only.");
}
