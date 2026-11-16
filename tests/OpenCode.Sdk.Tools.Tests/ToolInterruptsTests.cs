namespace OpenCode.Sdk.Tools.Tests;

public sealed class ToolInterruptsTests
{
    [Test]
    public async Task RunAsync_Should_Propagate_The_Run_Exception_And_Dispose_Both_Registrations()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var interrupts = new ToolInterrupts(_ => first, _ => second);
        var failure = new InvalidOperationException("run failed");

        var exception = await Assert.That(async () =>
            _ = await interrupts.RunAsync(_ => Task.FromException<int>(failure))).Throws<InvalidOperationException>();

        await Assert.That(exception).IsSameReferenceAs(failure);
        await Assert.That(() => _ = first.Token).Throws<ObjectDisposedException>();
        await Assert.That(() => _ = second.Token).Throws<ObjectDisposedException>();
    }

    [Test]
    [Arguments("terminate")]
    [Arguments("ctrl-c")]
    public async Task RunAsync_Should_Ignore_A_Signal_After_The_Run_Ends(string signal)
    {
        var callbacks = new Dictionary<string, Action>(StringComparer.Ordinal);
        using var registration = new CancellationTokenSource();
        using var interrupts = new ToolInterrupts(
            callback => { callbacks.Add("ctrl-c", callback); return registration; },
            callback => { callbacks.Add("terminate", callback); return registration; });

        await Assert.That(await interrupts.RunAsync(_ => Task.FromResult(5))).IsEqualTo(5);

        await Assert.That(() => callbacks[signal]()).ThrowsNothing();
    }

    [Test]
    [Arguments("terminate")]
    [Arguments("ctrl-c")]
    [Arguments("none")]
    public async Task RunAsync_Should_Preserve_The_Run_Outcome_And_Dispose_Both_Registrations(string ending)
    {
        using var ctrlCRegistration = new CancellationTokenSource();
        using var terminateRegistration = new CancellationTokenSource();
        var callbacks = new Dictionary<string, Action>(StringComparer.Ordinal);
        using var interrupts = new ToolInterrupts(
            callback => { callbacks.Add("ctrl-c", callback); return ctrlCRegistration; },
            callback => { callbacks.Add("terminate", callback); return terminateRegistration; });
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var operation = interrupts.RunAsync(async token =>
        {
            observed = token;
            using var registration = token.Register(() => completion.TrySetResult(37));
            // Cancellation completes the run with its own result; this wait only guards a hang.
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        });
        try
        {
            await Assert.That(callbacks).ContainsKey("ctrl-c");
            await Assert.That(callbacks).ContainsKey("terminate");
            if (ending is "terminate" or "ctrl-c")
            {
                callbacks[ending]();
            }
            else
            {
                completion.SetResult(37);
            }

            await Assert.That(await operation.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(37);
            await Assert.That(observed.IsCancellationRequested).IsEqualTo(ending is "terminate" or "ctrl-c");
            await Assert.That(() => _ = ctrlCRegistration.Token).Throws<ObjectDisposedException>();
            await Assert.That(() => _ = terminateRegistration.Token).Throws<ObjectDisposedException>();
        }
        finally
        {
            _ = completion.TrySetResult(37);
            _ = await operation;
        }
    }
}
