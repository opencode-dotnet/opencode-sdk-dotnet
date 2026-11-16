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
        using var interrupted = new SemaphoreSlim(0);
        // Signal threads only record the request; cancellation callbacks run in the main flow.
        using var ctrlC = _registerCtrlC(() => interrupted.Release());
        using var terminate = _registerTerminate(() => interrupted.Release());
        var running = run(cancellation.Token);
        if (await Task.WhenAny(running, interrupted.WaitAsync()).ConfigureAwait(false) != running)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        return await running.ConfigureAwait(false);
    }

    public void Dispose() => Console.CancelKeyPress -= _ctrlC;
}
