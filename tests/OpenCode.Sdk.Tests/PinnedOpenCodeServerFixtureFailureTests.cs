using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using OpenCode.Sdk.TestSupport.Ownership;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Pins the failure-path log-retention contract: a real <see cref="PinnedOpenCodeServerFixture"/>
/// failure retains bounded stdout/stderr and identity beneath the results root, beyond the
/// launcher's own in-memory collector. Drives the fixture itself (its internal command-override
/// seam), not the shared per-session instance the other fixture tests share. Where a proof holds
/// the fixture's teardown deadline, it first waits for the owned server's actual disposal to
/// settle, so the final output it reads back is the launcher's final collection.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PinnedOpenCodeServerFixtureFailureTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeAsync_Should_Not_Label_Another_Body_Failure_As_Teardown(bool captureFails)
    {
        var captureFailure = new IOException("Shared fixture stderr capture failed.");
        var scenario = new ServerDiagnosticScenario { CaptureFailure = captureFails ? captureFailure : null };
        var fixture = scenario.CreateFixture();
        var first = new InvalidOperationException("First distinct body failure.");
        var second = new IOException("Second distinct body failure.");
        try
        {
            await fixture.InitializeAsync();
            fixture.MarkFailure(first, "first consumer", "phase=first body");
            fixture.MarkFailure(second, "second consumer", "phase=second body");

            var observed = await Assert.That(async () => await fixture.DisposeAsync()).Throws<InvalidOperationException>();

            await Assert.That(observed).IsSameReferenceAs(first);
            var metadata = await scenario.ReadLogAsync(fixture, "2.log");
            await Assert.That(metadata).Contains("test=second consumer");
            await Assert.That(metadata).Contains(second.Message);
            await Assert.That(metadata).DoesNotContain(first.Message);
            await Assert.That(metadata).DoesNotContain("teardown:");
            if (captureFails)
            {
                await Assert.That(metadata).Contains("lifecycle:");
                await Assert.That(metadata).Contains(captureFailure.Message);
            }
            else
            {
                await Assert.That(metadata).DoesNotContain("lifecycle:");
            }

            await Assert.That(await scenario.ReadLogAsync(fixture, "stdout.log")).Contains(ServerDiagnosticScenario.FinalStandardOutput);
        }
        finally
        {
            await scenario.DisposeFixtureAsync(fixture, Task.CompletedTask);
        }
    }

    [Test]
    public async Task DisposeAsync_Should_Create_Identity_Metadata_For_A_Capture_Only_Failure()
    {
        var failure = new IOException("Only stderr capture failed.");
        var scenario = new ServerDiagnosticScenario { CaptureFailure = failure, RetainLogs = true };
        var fixture = scenario.CreateFixture();
        try
        {
            await fixture.InitializeAsync();

            var observed = await Assert.That(async () => await fixture.DisposeAsync()).Throws<IOException>();

            await Assert.That(observed).IsSameReferenceAs(failure);
            await Assert.That(await scenario.ReadLogAsync(fixture, "stdout.log")).Contains(ServerDiagnosticScenario.FinalStandardOutput);
            var metadata = await scenario.ReadMetadataAsync(fixture);
            await Assert.That(metadata).Contains("test=fixture disposal");
            await Assert.That(metadata).Contains("framework=");
            await Assert.That(metadata).Contains("mode=owned");
            await Assert.That(metadata).Contains("phase=server diagnostic capture");
            await Assert.That(metadata).Contains(failure.Message);
        }
        finally
        {
            await scenario.DisposeFixtureAsync(fixture, Task.CompletedTask);
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task DisposeAsync_Should_Retain_A_Late_Capture_Failure_Without_Delaying_Other_Artifacts(CancellationToken cancellationToken)
    {
        using var barrier = new DiagnosticWriteBarrier();
        var captureFailure = new IOException("Late stderr write failure.");
        var scenario = new ServerDiagnosticScenario { CaptureBarrier = barrier, CaptureFailure = captureFailure };
        var deadline = scenario.Deadlines.Hold("pinned server stderr capture");
        await using var fixture = scenario.CreateFixture();
        var primary = new InvalidOperationException("Deliberate body failure before late capture.");
        var completion = Task.CompletedTask;
        try
        {
            await fixture.InitializeAsync();
            fixture.MarkFailure(primary, "late capture body", "pty-phase=initial READY");
            completion = fixture.DisposeAsync().AsTask();
            await barrier.Entered.WaitAsync(cancellationToken);
            await deadline.Entered.WaitAsync(cancellationToken);
            deadline.Release();

            var observed = await Assert.That(async () => await completion).Throws<InvalidOperationException>();
            await Assert.That(observed).IsSameReferenceAs(primary);
            await Assert.That(await scenario.ReadLogAsync(fixture, "stdout.log")).Contains(ServerDiagnosticScenario.FinalStandardOutput);
            await Assert.That(await scenario.ReadMetadataAsync(fixture)).Contains("pinned server stderr capture");
            barrier.Release();
            await scenario.Deadlines.DrainAsync(completion);
            await fixture.DrainDiagnosticsAsync(cancellationToken);
            var late = primary.Data[OwnedCleanup.LateFailuresKey]
                as System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<string, Exception>>;
            await Assert.That(late!.Single().Value).IsSameReferenceAs(captureFailure);
        }
        finally
        {
            barrier.Release();
            await scenario.DisposeFixtureAsync(fixture, completion);
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task DisposeAsync_Should_Report_A_Teardown_Timeout_Without_A_Body_Failure(CancellationToken cancellationToken)
    {
        var scenario = new ServerDiagnosticScenario();
        var teardown = scenario.Deadlines.Hold("pinned server teardown");
        await using var fixture = scenario.CreateFixture();
        var completion = Task.CompletedTask;
        try
        {
            await fixture.InitializeAsync();
            completion = fixture.DisposeAsync().AsTask();
            await teardown.Entered.WaitAsync(cancellationToken);

            // The owned server's disposal runs on regardless of the held deadline; let it settle
            // (its final output is then collected) before the controlled timeout is delivered.
            _ = await Task.WhenAny(await teardown.Operation);
            teardown.Expire();
            await teardown.Won.WaitAsync(cancellationToken);
            teardown.Deliver();

            var observed = await Assert.That(async () => await completion).Throws<TimeoutException>();
            await Assert.That(observed!.Message).Contains("pinned server teardown");
            await Assert.That(observed.InnerException).IsSameReferenceAs(teardown.Failure);
            await fixture.DrainDiagnosticsAsync(cancellationToken);
            await Assert.That(await scenario.ReadLogAsync(fixture, "stderr.log")).Contains(ServerDiagnosticScenario.FinalStandardError);
            await Assert.That(await scenario.ReadMetadataAsync(fixture)).Contains("pinned server teardown");
        }
        finally
        {
            await scenario.DisposeFixtureAsync(fixture, completion);
        }
    }

    [Test]
    public async Task DisposeAsync_Should_Capture_Final_Output_And_Preserve_The_Body_Failure()
    {
        var scenario = new ServerDiagnosticScenario();
        var fixture = scenario.CreateFixture();
        var primary = new InvalidOperationException("Deliberate live body failure.");
        try
        {
            await fixture.InitializeAsync();
            fixture.MarkFailure(primary, "controlled body", "pty-phase=initial READY; pty-id=controlled-pty");

            var observed = await Assert.That(async () => await fixture.DisposeAsync()).Throws<InvalidOperationException>();

            await Assert.That(observed).IsSameReferenceAs(primary);
            await Assert.That(primary.Data[OwnedCleanup.FailuresKey]).IsNull();
            await Assert.That(await scenario.ReadLogAsync(fixture, "stdout.log")).Contains(ServerDiagnosticScenario.FinalStandardOutput);
            await Assert.That(await scenario.ReadLogAsync(fixture, "stderr.log")).Contains(ServerDiagnosticScenario.FinalStandardError);
            var metadata = await scenario.ReadMetadataAsync(fixture);
            await Assert.That(metadata).Contains("controlled body");
            await Assert.That(metadata).Contains("framework=");
            await Assert.That(metadata).Contains("pty-phase=initial READY");
        }
        finally
        {
            await scenario.DisposeFixtureAsync(fixture, Task.CompletedTask);
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task DisposeAsync_Should_Preserve_Teardown_And_Capture_Failures_With_Available_Output(CancellationToken cancellationToken)
    {
        var captureFailure = new IOException("Deliberate stderr capture failure.");
        var scenario = new ServerDiagnosticScenario { CaptureFailure = captureFailure };
        var deadline = scenario.Deadlines.Hold("pinned server teardown");
        var fixture = scenario.CreateFixture();
        var primary = new InvalidOperationException("Deliberate live body failure.");
        var completion = Task.CompletedTask;
        try
        {
            await fixture.InitializeAsync();
            fixture.MarkFailure(primary, "controlled teardown", "pty-phase=initial READY");
            completion = fixture.DisposeAsync().AsTask();
            await deadline.Entered.WaitAsync(cancellationToken);

            // Let the owned server's disposal settle so its final output is collected, then
            // deliver the controlled teardown timeout on top of the body failure.
            _ = await Task.WhenAny(await deadline.Operation);
            deadline.Expire();
            await deadline.Won.WaitAsync(cancellationToken);
            deadline.Deliver();

            var observed = await Assert.That(async () => await completion).Throws<InvalidOperationException>();
            await Assert.That(observed).IsSameReferenceAs(primary);
            var failures = primary.Data[OwnedCleanup.FailuresKey] as AggregateException;
            await Assert.That(failures!.InnerExceptions).Contains(captureFailure);
            await Assert.That(failures.InnerExceptions.OfType<TimeoutException>().Any()).IsTrue();
            await Assert.That(await scenario.ReadLogAsync(fixture, "stdout.log")).Contains(ServerDiagnosticScenario.FinalStandardOutput);
            await Assert.That(await scenario.ReadMetadataAsync(fixture)).Contains("Deliberate live body failure");
        }
        finally
        {
            await scenario.DisposeFixtureAsync(fixture, completion);
        }
    }

    [Test]
    public async Task InitializeAsync_Should_Retain_Logs_Under_The_Results_Root_On_Failure()
    {
        var scenario = new ServerDiagnosticScenario();
        await using var fixture = scenario.CreateFixture("Server.exited-diagnostic-peer.js");
        try
        {
            var startup = await Assert.That(fixture.InitializeAsync).Throws<OpenCodeServerException>();
            var disposal = await Assert.That(async () => await fixture.DisposeAsync()).Throws<OpenCodeServerException>();
            await Assert.That(disposal).IsSameReferenceAs(startup);
            await Assert.That(await scenario.ReadLogAsync(fixture, "stderr.log")).Contains("FINAL-STDERR");
            await Assert.That(await scenario.ReadMetadataAsync(fixture)).Contains("phase=server startup");
        }
        finally
        {
            await scenario.DisposeFixtureAsync(fixture, Task.CompletedTask);
        }
    }
}
