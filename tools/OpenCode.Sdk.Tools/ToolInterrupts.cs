using System.Runtime.InteropServices;

namespace OpenCode.Sdk.Tools;

/// <summary>Owns the tool's interrupt registrations for the lifetime of one run.</summary>
internal sealed class ToolInterrupts : IDisposable
{
    private readonly Func<Action, IDisposable> _registerCtrlC;
    private readonly Func<Action, IDisposable> _registerTerminate;
    private ConsoleCancelEventHandler? _ctrlC;

    public ToolInterrupts()
    {
        _registerCtrlC = callback =>
        {
            _ctrlC = (_, e) =>
            {
                e.Cancel = true;
                callback();
            };
            Console.CancelKeyPress += _ctrlC;
            return this;
        };
        _registerTerminate = callback => PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            callback();
        });
    }

    internal ToolInterrupts(Func<Action, IDisposable> registerCtrlC, Func<Action, IDisposable> registerTerminate)
    {
        _registerCtrlC = registerCtrlC;
        _registerTerminate = registerTerminate;
    }

    public async Task<int> RunAsync(Func<CancellationToken, Task<int>> run)
    {
        using var cancellation = new CancellationTokenSource();
        // Signal threads only record the request; cancellation callbacks run in the main flow. The
        // runtime can still call a handler it copied before its registration was disposed, so the
        // request is a completion source that is never disposed and ignores a late call.
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctrlC = _registerCtrlC(() => interrupted.TrySetResult());
        using var terminate = _registerTerminate(() => interrupted.TrySetResult());
        var running = run(cancellation.Token);
        if (await Task.WhenAny(running, interrupted.Task).ConfigureAwait(false) != running)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        return await running.ConfigureAwait(false);
    }

    public void Dispose() => Console.CancelKeyPress -= _ctrlC;
}
