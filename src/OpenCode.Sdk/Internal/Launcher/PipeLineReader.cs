using System.Text;
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// Reads one of a POSIX child's output pipes line by line, asynchronously: on .NET for Linux and
/// macOS a pipe read is socket-backed and holds no thread while it waits. Lines split the way
/// <c>Process</c>'s own readers split them, on <c>\n</c>, <c>\r</c>, or <c>\r\n</c>, with an empty
/// line delivered as an empty string and a final unterminated line delivered at end-of-stream. The
/// reader owns its stream and closes it when it ends; disposing the reader releases it early.
/// </summary>
internal sealed class PipeLineReader : IAsyncDisposable
{
    private const int BufferSize = 4096;

    private readonly Stream _stream;
    private readonly Action<string> _onLine;
    private readonly CancellationTokenSource _release = new();
    private readonly Task<bool> _completion;
    private int _released;

    private PipeLineReader(Stream stream, Action<string> onLine)
    {
        _stream = stream;
        _onLine = onLine;
        _completion = Task.Run(ReadAsync, CancellationToken.None);
    }

    /// <summary>
    /// Gets a task that completes once the reader has ended: true when the stream reached
    /// end-of-stream, false when the read was released or failed.
    /// </summary>
    public Task<bool> Completion => _completion;

    /// <summary>Starts delivering every line of <paramref name="stream"/> to <paramref name="onLine"/>.</summary>
    /// <param name="stream">The parent's read end of the pipe; the reader closes it when it ends.</param>
    /// <param name="onLine">Receives each line, on a thread-pool thread.</param>
    /// <returns>The running reader.</returns>
    public static PipeLineReader Start(Stream stream, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);
        return new PipeLineReader(stream, onLine);
    }

    /// <summary>
    /// Ends the reader whether or not the stream reached end-of-stream: a pending read is canceled
    /// and the stream is closed, which also ends a read no token reaches on the downlevel targets.
    /// It does not wait for <see cref="Completion"/>.
    /// </summary>
    /// <returns>A task that completes once the release was issued.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) is 1)
        {
            return;
        }

        try
        {
            await _release.CancelOnWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            _release.Dispose();
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    [SlopwatchSuppress(
        "SW003",
        "The caught read failures are this reader's end condition: a released read, a broken pipe, or a stream already closed all mean the stream is over, which Completion reports.")]
    private async Task<bool> ReadAsync()
    {
        try
        {
            // The reader closes the stream when it is disposed, at end-of-stream and on failure alike.
            using var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, BufferSize);
            while (true)
            {
#if NET
                var line = await reader.ReadLineAsync(_release.Token).ConfigureAwait(false);
#else
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
                if (line is null)
                {
                    return true;
                }

                _onLine(line);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A released read, a broken pipe, or a stream the release already closed: each ends
            // this reader, which is what its owner is waiting for.
            return false;
        }
    }
}
