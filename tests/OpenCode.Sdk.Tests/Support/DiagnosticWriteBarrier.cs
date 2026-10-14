namespace OpenCode.Sdk.Tests.Support;

/// <summary>Controls a synchronous filesystem callback executed by the owned capture worker.</summary>
internal sealed class DiagnosticWriteBarrier : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable VSTHRD003 // Test signals complete on their own source and resume asynchronously; exposing the task is the signal's contract.
    public Task Entered => _entered.Task;
#pragma warning restore VSTHRD003

    public void Block()
    {
        _ = _entered.TrySetResult(true);
        _release.Wait();
    }

    public void Release() => _release.Set();

    public void Dispose() => _release.Dispose();
}
