namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>
/// Serves one prepared prefix and then fails the way a connection dropped mid-body does,
/// so a test can reach the frames that arrived before the failure.
/// </summary>
internal sealed class FaultingStream : Stream
{
    private readonly byte[] _prefix;
    private int _offset;

    public FaultingStream(byte[] prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        _prefix = prefix;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_offset >= _prefix.Length)
        {
            throw new IOException("Unable to read data from the transport connection.");
        }

        var length = Math.Min(_prefix.Length - _offset, count);
        Array.Copy(_prefix, _offset, buffer, offset, length);
        _offset += length;
        return length;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
