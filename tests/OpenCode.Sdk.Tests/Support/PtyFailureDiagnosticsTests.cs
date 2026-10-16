using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PtyFailureDiagnosticsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Timeout(30_000)]
    public async Task CaptureStatusAsync_Should_Preserve_Caller_Failure_And_External_Ownership(bool stall, CancellationToken cancellationToken)
    {
        var fileSystem = new RealFileSystem();
        await using var peer = new PtyDiagnosticServer(stall);
        var server = peer.Server;
        await using var fixture = new PinnedOpenCodeServerFixture(new ExternalServerEndpoint(server.Endpoint, "test-password"));
        var deadlines = new OperationDeadlineScenario();
        var held = stall ? deadlines.Hold("PTY failure status lookup") : null;
        var diagnostics = new PtyFailureDiagnostics(deadlines.Deadline);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelOnWorkerAsync();
        var primary = new OperationCanceledException("Original body cancellation.", cancelled.Token);
        var capture = Task.CompletedTask;
        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateClient();
            diagnostics.Created("pty_100", 4242, 200);
            diagnostics.Enter("initial READY");
            capture = diagnostics.CaptureStatusAsync(fixture, null, primary);
            await peer.Requested.WaitAsync(cancellationToken);
            if (held is not null)
            {
                await held.Entered.WaitAsync(cancellationToken);
                held.Release();
            }

            await capture;
            fixture.MarkFailure(primary, "external PTY failure", diagnostics.Describe());
            var observed = await Assert.That(async () => await fixture.DisposeAsync()).Throws<OperationCanceledException>();
            await Assert.That(observed).IsSameReferenceAs(primary);
            await Assert.That(fileSystem.Directory.GetFiles(fixture.DiagnosticsDirectory, "stdout.log").Length).IsEqualTo(0);
            await Assert.That(fileSystem.Directory.GetFiles(fixture.DiagnosticsDirectory, "stderr.log").Length).IsEqualTo(0);
            var health = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);
            await Assert.That(health.Status).IsEqualTo(200);
            if (stall)
            {
                var lateReport = diagnostics.LateFailures ?? throw new InvalidOperationException("The status deadline did not retain its operation.");
                await lateReport.WaitForAllAsync(cancellationToken);
                await server.ClientDisconnected.WaitAsync(cancellationToken);
                await Assert.That(primary.Data[OwnedCleanup.FailuresKey]).IsTypeOf<AggregateException>();
            }
            else
            {
                await Assert.That(diagnostics.Describe()).Contains("http=200 status=running pid=4242");
            }
        }
        finally
        {
            server.ReleaseResponses();
            await deadlines.DrainAsync(capture);
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (diagnostics.LateFailures is { } late)
                {
                    await late.WaitForAllAsync(cleanup.Token);
                }

                await deadlines.DrainAsync(fixture.DisposeAsync().AsTask());
            }
            finally
            {
                fixture.RunRoot.Dispose();
            }
        }
    }
}
