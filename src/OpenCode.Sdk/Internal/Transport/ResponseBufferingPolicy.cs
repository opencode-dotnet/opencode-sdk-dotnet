using System.Buffers;
using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Buffers the response body into a pooled array under the progress window: one linked timer
/// spans the send and every read, each read that progresses re-arms it, and interrupting a
/// stalled read disposes the content so even a read no token can reach settles. Only a live
/// event-stream success stays unbuffered, and it leaves this policy with the timer already
/// dead. Knowledge source: BCL-derived — HttpClient's timeout linking and dotnet/runtime's
/// pooled response buffering are the reference designs.
/// </summary>
internal sealed class ResponseBufferingPolicy : PipelinePolicy
{
    /// <summary>The smallest rent, used when the response declares no usable length.</summary>
    private const int InitialRentBytes = 256;

    /// <summary>The largest array the runtime can allocate; growth clamps here and lets the pool refuse beyond it.</summary>
    private const int MaxArrayLength = 0x7FFFFFC7;

#if !NET
    /// <summary>The largest array retained by the downlevel shared pool.</summary>
    private const int SharedMaxArrayLength = 1_048_576;

    /// <summary>The largest array retained by the downlevel large-response pool.</summary>
    private const int LargeMaxArrayLength = 16 * 1024 * 1024;

    /// <summary>The retention limit for each large-response size class.</summary>
    private const int LargeMaxArraysPerBucket = 2;
#endif

    internal static ArrayPool<byte> DefaultPool { get; } =
#if NET
        ArrayPool<byte>.Shared;
#else
        new DownlevelResponsePool(ArrayPool<byte>.Shared, ArrayPool<byte>.Create(LargeMaxArrayLength, LargeMaxArraysPerBucket));
#endif

    private readonly ArrayPool<byte> _pool;
    private readonly TimeProvider _time;

    public ResponseBufferingPolicy(ArrayPool<byte> pool, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(time);

        _pool = pool;
        _time = time;
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, ReadOnlyMemory<PipelinePolicy> remaining)
    {
        // The window is linked over caller and client lifetime and armed before the send, so a
        // server that never answers headers is bounded exactly like a stalled body. The
        // transport sends under NetworkToken; classification keeps caller and lifetime apart.
        using var progress = new ProgressWindow(message.NetworkTimeout, _time, message.CancellationToken, message.LifetimeToken);
        message.NetworkToken = progress.Token;
        progress.Restart();

        // A streamed request body restarts the window as each chunk goes out, so the send is
        // bounded by progress exactly like the read.
        message.ProgressWindow = progress;
        try
        {
            await ProcessNextAsync(message, remaining).ConfigureAwait(false);
        }
        finally
        {
            message.ProgressWindow = null;
        }

        Debug.Assert(message.Response is not null, "The transport writes the response before this policy resumes.");
        if (ShouldBuffer(message))
        {
            message.Body = await ReadAsync(message, progress).ConfigureAwait(false);
        }
    }

    private static bool ShouldBuffer(PipelineMessage message)
    {
        // The one-shot plane buffers every response — an unexpected no-content body is
        // drained here and ignored by the materializer (canon). A 2xx on the stream plane is
        // either the live event stream, which stays open until the caller is done with it, or
        // an undeclared success the plane refuses without reading; everything else is an
        // error body, buffered under the same window as any one-shot body.
        return message.BufferBody || (int)message.Response!.StatusCode is < 200 or >= 300;
    }

    private async Task<ResponseBody> ReadAsync(PipelineMessage message, ProgressWindow progress)
    {
        var content = message.Response!.Content;
        if (content is null)
        {
            return ResponseBody.Empty;
        }

        // A read a token cannot reach still has to settle: downlevel socket reads ignore a
        // token once in flight, and HttpContent's own buffering path drops the token before
        // it reaches SerializeToStreamAsync. Interrupting — progress expiry or caller
        // cancellation or client disposal alike — therefore disposes the content to fail the
        // pending read. Classification keeps caller identity and client disposal apart from
        // progress expiry, which remains a transport failure.
        // API-shape divergence, not algorithm: the registration has no DisposeAsync downlevel.
#if NET
        await using var interruption = progress.Token.UnsafeRegister(
            static state => ((HttpContent)state!).Dispose(),
            content).ConfigureAwait(false);
#else
        using var interruption = progress.Token.UnsafeRegister(
            static state => ((HttpContent)state!).Dispose(),
            content);
#endif
        try
        {
            progress.Restart();
            var body = await content.ReadAsStreamAsync(progress.Token).ConfigureAwait(false);
            return await CopyToPooledAsync(body, content.Headers.ContentLength, progress).ConfigureAwait(false);
        }
        catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.ResponseBodyRead))
        {
            throw FailureClassification.Map(exception, FailurePhase.ResponseBodyRead, message.CancellationToken, message.LifetimeToken);
        }
        catch (Exception exception) when (progress.IsCancellationRequested)
        {
            // The interruption above disposed the content while HttpContent's own buffering path
            // was still materializing it; the BCL then fails with whatever its torn-down state
            // yields rather than ObjectDisposedException. Knowledge source: on .NET 10 the buffer
            // is a pooled stream that Dispose returns, and the read stream is then built over a
            // null array (ArgumentNullException) when disposal lands mid-materialization. The
            // interruption is the cause, so the fault is classified as one — caller cancellation,
            // client disposal or the window expiring.
            throw FailureClassification.MapFault(exception, FailurePhase.ResponseBodyRead, message.CancellationToken, message.LifetimeToken);
        }
    }

    private async Task<ResponseBody> CopyToPooledAsync(Stream body, long? declaredLength, ProgressWindow progress)
    {
        var bytes = _pool.Rent(InitialRent(declaredLength));
        try
        {
            var filled = 0;
            while (true)
            {
                if (filled == bytes.Length)
                {
                    bytes = Grow(bytes, filled);
                }

                var read = await body.ReadAsync(bytes.AsMemory(filled), progress.Token).ConfigureAwait(false);
                if (read is 0)
                {
                    // Ownership moves to the message; PipelineMessage.Dispose returns the array.
                    return new ResponseBody(_pool, bytes, filled);
                }

                filled += read;
                progress.Restart();
            }
        }
        catch
        {
            // A failed read has settled before this runs — the copy is never abandoned
            // mid-write — so the array is safe to hand back.
            _pool.Return(bytes);
            throw;
        }
    }

    /// <summary>
    /// Sizes the first rent from the declared length plus the end-of-stream probe byte, so a
    /// correctly declared body never grows; an undeclared length starts small and doubles.
    /// </summary>
    private static int InitialRent(long? declaredLength) =>
        declaredLength is > 0
            ? (int)Math.Clamp(declaredLength.Value + 1, InitialRentBytes, MaxArrayLength)
            : InitialRentBytes;

    private byte[] Grow(byte[] bytes, int filled)
    {
        if (bytes.Length == MaxArrayLength)
        {
            // A body beyond the largest possible array cannot buffer; fail as the read
            // failure it is instead of looping on a rent that can never be bigger.
            throw new IOException("The opencode response body exceeds the largest bufferable size.");
        }

        var grown = _pool.Rent((int)Math.Min(bytes.Length * 2L, MaxArrayLength));
        bytes.AsSpan(0, filled).CopyTo(grown);
        _pool.Return(bytes);
        return grown;
    }

#if !NET
    /// <summary>
    /// Keeps rents up to 1 MiB on the shared pool, whose downlevel form retains nothing larger,
    /// and routes larger rents, and returns by array length, to a dedicated large pool. The large
    /// pool retains returned arrays without trimming, bounded by two arrays in each
    /// large size class (2, 4, 8 and 16 MiB): about 60 MiB worst case, and in practice one array
    /// per size class the process has used. A body above 16 MiB still allocates, as every body
    /// above 1 MiB did before.
    /// </summary>
    internal sealed class DownlevelResponsePool : ArrayPool<byte>
    {
        private readonly ArrayPool<byte> _shared;
        private readonly ArrayPool<byte> _large;

        internal DownlevelResponsePool(ArrayPool<byte> shared, ArrayPool<byte> large)
        {
            _shared = shared;
            _large = large;
        }

        public override byte[] Rent(int minimumLength) =>
            (minimumLength > SharedMaxArrayLength ? _large : _shared).Rent(minimumLength);

        public override void Return(byte[] array, bool clearArray = false)
        {
            ArgumentNullException.ThrowIfNull(array);
            (array.Length > SharedMaxArrayLength ? _large : _shared).Return(array, clearArray);
        }
    }
#endif
}
