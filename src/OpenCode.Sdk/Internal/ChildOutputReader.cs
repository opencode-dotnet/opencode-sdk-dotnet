using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads one redirected stream of a launched child line by line on a dedicated thread, never on
/// the thread pool. On Windows before .NET 11, <see cref="System.Diagnostics.Process"/> creates
/// the redirected pipes synchronous, so every pending read blocks the thread it runs on until the
/// child writes; on a pool thread that holds a worker for the server's whole life
/// (dotnet/runtime#81896). The read is cancellable: <see cref="CancelPendingRead"/> sets the stop
/// flag and cancels a read the thread is blocked in (<c>CancelSynchronousIo</c>), which is how
/// the owner releases the thread when end-of-stream never arrives.
/// </summary>
internal sealed class ChildOutputReader
{
    /// <summary>Reader threads started in this process and not yet ended.</summary>
    private static int s_liveReaders;

    private readonly TextReader _reader;
    private readonly Action<string> _onLine;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _threadHandleGate = new();
    private IntPtr _threadHandle;
    private int _canceled;

    private ChildOutputReader(TextReader reader, Action<string> onLine)
    {
        _reader = reader;
        _onLine = onLine;
    }

    /// <summary>
    /// Gets a task that completes once the reading thread has ended: true when the stream reached
    /// end-of-stream, false when the read was canceled or failed.
    /// </summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>
    /// Gets how many reader threads started in this process have not ended yet; friend-assembly
    /// test seam for the guard that no reader outlives the test session.
    /// </summary>
    internal static int LiveReaders => Volatile.Read(ref s_liveReaders);

    /// <summary>Starts a background thread that delivers every line of <paramref name="reader"/> to <paramref name="onLine"/>.</summary>
    /// <param name="reader">The redirected stream's reader; the reading thread disposes it when it ends.</param>
    /// <param name="onLine">Receives each line, on the reading thread.</param>
    /// <param name="threadName">The thread's name, so a dump names what the thread reads.</param>
    /// <returns>The started reader.</returns>
    public static ChildOutputReader Start(TextReader reader, Action<string> onLine, string threadName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onLine);

        var outputReader = new ChildOutputReader(reader, onLine);
        var thread = new Thread(outputReader.Run) { IsBackground = true, Name = threadName };
        _ = Interlocked.Increment(ref s_liveReaders);
        thread.Start();
        return outputReader;
    }

    /// <summary>
    /// Stops the reader: no further read starts, and a read the thread is blocked in is canceled.
    /// A cancel can land between two reads and find nothing to cancel, so the owner repeats it
    /// until <see cref="Completion"/> completes.
    /// </summary>
    public void CancelPendingRead()
    {
        _ = Interlocked.Exchange(ref _canceled, 1);
        lock (_threadHandleGate)
        {
            if (_threadHandle != IntPtr.Zero)
            {
                // False means no synchronous I/O was pending at this instant; the owner retries.
                _ = LauncherInterop.Kernel32.CancelSynchronousIo(_threadHandle);
            }
        }
    }

    [SlopwatchSuppress(
        "SW003",
        "The caught read failures are this thread's end condition: a canceled read, a broken pipe, or a disposed reader all mean the stream is over, which Completion reports.")]
    private void Run()
    {
        OpenOwnThreadHandle();
        var endOfStream = false;
        try
        {
            while (Volatile.Read(ref _canceled) is 0)
            {
                var line = _reader.ReadLine();
                if (line is null)
                {
                    endOfStream = true;
                    break;
                }

                _onLine(line);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A canceled read (ERROR_OPERATION_ABORTED), a broken pipe, or a reader the owner
            // already disposed: each ends this thread, which is what the owner is waiting for.
        }
        finally
        {
            // The thread that read the stream is its only user, so it releases it: on Windows the
            // pipe's read handle. Process.Dispose never closes a redirected stream read
            // synchronously, so nothing else would before the finalizer.
            _reader.Dispose();
            CloseOwnThreadHandle();

            // Counted down before completion is signaled, so an owner that awaited Completion
            // already sees this thread gone.
            _ = Interlocked.Decrement(ref s_liveReaders);
            _ = _completion.TrySetResult(endOfStream);
        }
    }

    private void OpenOwnThreadHandle()
    {
        if (!LauncherInterop.IsWindows)
        {
            return;
        }

        var handle = LauncherInterop.Kernel32.OpenThread(
            LauncherInterop.ThreadTerminate,
            inheritHandle: false,
            LauncherInterop.Kernel32.GetCurrentThreadId());
        lock (_threadHandleGate)
        {
            _threadHandle = handle;
        }
    }

    private void CloseOwnThreadHandle()
    {
        lock (_threadHandleGate)
        {
            if (_threadHandle != IntPtr.Zero)
            {
                _ = LauncherInterop.Kernel32.CloseHandle(_threadHandle);
                _threadHandle = IntPtr.Zero;
            }
        }
    }
}
