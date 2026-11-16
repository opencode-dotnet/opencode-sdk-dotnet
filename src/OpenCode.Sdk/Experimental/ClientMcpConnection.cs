using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>A caller-owned reconnecting socket, with no readiness promise. Late replies stay on their original socket.</summary>
/// <remarks>Disposal joins only SDK I/O. A retry-factory error rethrows on the timer callback and can terminate the process.</remarks>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public sealed class ClientMcpConnection : IAsyncDisposable
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly List<Task> _loops = [];
    private readonly TimeProvider _time;
    private readonly Func<ITerminalWebSocket> _create;
    private readonly Func<ITerminalWebSocket, CancellationToken, Task<ITerminalWebSocket>> _upgrade;
    private readonly ClientMcpResponder _responder;
    private readonly string _name;
    private readonly Action<int, TimeSpan>? _retryScheduled;
    private readonly Action<ClientMcpDispatch>? _dispatchCreated;
    private ClientMcpDispatch? _dispatch;
    private ITimer? _timer;
    private Task? _disposal;
    private bool _disposed;
    private int _delay = 500;
    private int _attempt;

    internal ClientMcpConnection(ClientMcpOptions options, TimeProvider time, Func<ITerminalWebSocket> create,
        Func<ITerminalWebSocket, CancellationToken, Task<ITerminalWebSocket>> upgrade,
        Action<int, TimeSpan>? retryScheduled = null, Action<ClientMcpDispatch>? dispatchCreated = null)
    {
        Completion = _completion.Task;
        _time = time;
        _create = create;
        _upgrade = upgrade;
        _retryScheduled = retryScheduled;
        _dispatchCreated = dispatchCreated;
        _name = options.Name;
        _responder = new ClientMcpResponder(options);
        var prepared = _create();
        _lifetime = new CancellationTokenSource();
        Start(prepared);
    }

    internal Task Completion { get; }

    /// <summary>Stops retries and closes this socket with 1000 without canceling or joining application work.</summary>
    /// <returns>The shared completion of owned socket and I/O cleanup.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is null)
            {
                _disposed = true;
                _timer?.Dispose();
                _dispatch?.Retire();
                _disposal = DisposeCoreAsync();
            }

            return new ValueTask(_disposal);
        }
    }

    private void Start(ITerminalWebSocket prepared)
    {
        var admission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed)
            {
                prepared.Dispose();
                return;
            }

            _attempt++;
            _loops.RemoveAll(static task => task.IsCompleted);
            var run = admission.Task.ContinueWith(_ => RunAsync(prepared), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            _loops.Add(run.ContinueWith(completed =>
            {
                if (completed.Exception is { } failure)
                {
                    _ = _completion.TrySetException(failure.InnerExceptions);
                    _ = Completion.Exception;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }

        _ = admission.TrySetResult(true);
    }

    private async Task RunAsync(ITerminalWebSocket prepared)
    {
        try
        {
            var socket = await _upgrade(prepared, _lifetime.Token).ConfigureAwait(false);
            var core = new TerminalSocketCore<ClientMcpMessage>(socket, new ClientMcpFrameDecoder(), new ClientMcpClosePolicy(_name),
                typeof(ClientMcpConnection), _time, label: "MCP")
            { SendTimeout = Timeout.InfiniteTimeSpan };
            await using var coreDisposal = core.ConfigureAwait(false);
            var dispatch = new ClientMcpDispatch(core, _responder, ResetBackoff);
            try
            {
                lock (_gate)
                {
                    _dispatch = dispatch;
                    if (_disposed)
                    {
                        dispatch.Retire();
                    }
                }

                _dispatchCreated?.Invoke(dispatch);
                await foreach (var frame in core.ReadAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    await dispatch.DispatchAsync(frame).ConfigureAwait(false);
                }
            }
            finally
            {
                dispatch.Retire();
            }
        }
        catch (Exception exception) when (exception is OpenCodeTransportException || FailureClassification.Handles(exception, FailurePhase.PtyWebSocketRead))
        {
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }
        }
        ScheduleRetry();
    }

    private void ResetBackoff()
    {
        lock (_gate)
        {
            _delay = 500;
        }
    }

    private void ScheduleRetry()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                _ = _completion.TrySetResult(true);
                return;
            }

            var delay = TimeSpan.FromMilliseconds(_delay);
            _delay = Math.Min(_delay * 2, 10000);
            _timer = _time.CreateTimer(_ => Retry(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _ = _timer.Change(delay, Timeout.InfiniteTimeSpan);
            _retryScheduled?.Invoke(_attempt + 1, delay);
        }
    }

    private void Retry()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed)
            {
                return;
            }

            try
            {
                Start(_create());
            }
            catch (Exception exception)
            {
                _ = _completion.TrySetException(exception);
                _ = Completion.Exception;
                throw;
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelOnWorkerAsync().ConfigureAwait(false);
        Task[] loops;
        lock (_gate)
        {
            loops = [.. _loops];
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
        _lifetime.Dispose();
        _ = _completion.TrySetResult(true);
    }
}
