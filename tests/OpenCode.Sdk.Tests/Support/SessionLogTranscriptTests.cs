using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

public sealed class SessionLogTranscriptTests
{
    [Test]
    public async Task ReadToEndAsync_Should_Preserve_The_Failure_With_Bounded_Observations()
    {
        var failure = new InvalidOperationException("log enumeration failed");
        var transcript = new SessionLogTranscript(new EventDiagnosticScenario { Failure = failure });

        var thrown = await Assert.That(async () =>
        {
            _ = await transcript.ReadToEndAsync(
                new SessionLogRequest { Follow = QueryBoolean.False }, CancellationToken.None);
        })
            .Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(failure);
        var diagnostic = (string)failure.Data[EventDiagnosticSummary.DataKey]!;
        await Assert.That(diagnostic.Length).IsLessThan(2000);
        await Assert.That(diagnostic).Contains("event-0000-");
        await Assert.That(diagnostic).Contains("event-0099-");
        await Assert.That(diagnostic).Contains("84 events omitted");
        await Assert.That(diagnostic).DoesNotContain("event-0050-");
    }

    [Test]
    public async Task ReadToEndAsync_Should_Preserve_The_Complete_Successful_Transcript()
    {
        var transcript = new SessionLogTranscript(new EventDiagnosticScenario());

        var items = await transcript.ReadToEndAsync(
            new SessionLogRequest { Follow = QueryBoolean.False }, CancellationToken.None);

        await Assert.That(items).Count().IsEqualTo(100);
        await Assert.That(items.All(item => item.Type.Length > 4000)).IsTrue();
        await Assert.That(items[50].Type).StartsWith("event-0050-");
    }

    [Test]
    public async Task ReadTurnAsync_Should_Bound_Follow_Diagnostics_Without_Truncating_Observed_Items()
    {
        var transcript = new SessionLogTranscript(new EventDiagnosticScenario { IncludeMarker = true });
        try
        {
            await transcript.AttachAsync(1, CancellationToken.None);
            var thrown = await Assert.That(() => transcript.ReadTurnAsync("ses_owned", "reply"))
                .Throws<InvalidOperationException>();

            await Assert.That(thrown!.Message.Length).IsLessThan(2000);
            await Assert.That(thrown.Message).Contains("log.synced");
            await Assert.That(thrown.Message).Contains("event-0099-");
            await Assert.That(thrown.Message).Contains("85 events omitted");
            await Assert.That(transcript.LiveItems).Count().IsEqualTo(100);
            await Assert.That(transcript.LiveItems[50].Type.Length).IsGreaterThan(4000);
        }
        finally
        {
            await transcript.DisposeAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Cleanup_Should_Bound_Stalled_Disposal_And_Retain_Its_Late_Failure()
    {
        var disposal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerator = new ControlledAsyncEnumerator(disposal.Task);
        using var window = new CancellationTokenSource();
        var transcript = new SessionLogTranscript(enumerator, window);
        var primaryFailure = new InvalidOperationException("primary failure");
        var removalFailure = new InvalidOperationException("removal failure");
        var lateDisposalFailure = new InvalidOperationException("late disposal failure");
        var removalTokenWasCancelled = true;
        var scenario = new OperationDeadlineScenario();
        var deadline = scenario.Hold("session log enumerator");
        var cleanup = new OwnedSessionCleanup(
            _ => Task.CompletedTask,
            token =>
            {
                removalTokenWasCancelled = token.IsCancellationRequested;
                return Task.FromException(removalFailure);
            },
            TimeSpan.FromMilliseconds(50), scenario.Deadline);
        cleanup.MarkTurnCompleted();
        cleanup.Own("session log enumerator", transcript.DisposeAsync);

        var completing = cleanup.CompleteAsync(primaryFailure);
        try
        {
            await enumerator.Entered;
            await deadline.Entered;
            deadline.Expire();
            deadline.Deliver();
            var thrown = await Assert.That(() => completing)
                .Throws<InvalidOperationException>();

            await Assert.That(thrown).IsSameReferenceAs(primaryFailure);
            await Assert.That(enumerator.DisposeCalls).IsEqualTo(1);
            await Assert.That(removalTokenWasCancelled).IsFalse();
            var failures = primaryFailure.Data[OwnedSessionCleanup.FailuresKey] as AggregateException;
            await Assert.That(failures).IsNotNull();
            await Assert.That(failures!.InnerExceptions).Count().IsEqualTo(2);
            await Assert.That(failures.InnerExceptions[0]).IsTypeOf<TimeoutException>();
            await Assert.That(failures.InnerExceptions[1]).IsSameReferenceAs(removalFailure);
            var diagnosticFailures = primaryFailure.Data[OwnedSessionCleanup.LateFailuresKey]
                as IReadOnlyCollection<KeyValuePair<string, Exception>>;
            await Assert.That(diagnosticFailures).IsNotNull();
            var late = cleanup.LateFailures;
            await Assert.That(late).IsNotNull();

            disposal.SetException(lateDisposalFailure);
            var observed = await late.WaitForFailureAsync(CancellationToken.None);

            await Assert.That(observed.Key).IsEqualTo("session log enumerator");
            await Assert.That(observed.Value).IsSameReferenceAs(lateDisposalFailure);
            await Assert.That(late.Failures).Count().IsEqualTo(1);
            await Assert.That(late.Failures[0].Value).IsSameReferenceAs(lateDisposalFailure);
            await Assert.That(diagnosticFailures!).Count().IsEqualTo(1);
            await Assert.That(diagnosticFailures!.Single().Key).IsEqualTo("session log enumerator");
            await Assert.That(diagnosticFailures.Single().Value).IsSameReferenceAs(lateDisposalFailure);
            _ = await Assert.That(window.Cancel).Throws<ObjectDisposedException>();
            Console.WriteLine(
                "session-log-cleanup: immediate-failures=" + failures.InnerExceptions.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
                " removal-token-cancelled=" + removalTokenWasCancelled.ToString() +
                " late-operation=" + observed.Key);
        }
        finally
        {
            _ = disposal.TrySetResult(true);
            await scenario.DrainAsync(completing);
            if (cleanup.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }

    private sealed class ControlledAsyncEnumerator(Task disposal) : IAsyncEnumerator<ISessionLogItem>
    {
        public ISessionLogItem Current => throw new InvalidOperationException("No item is available.");

        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            _ = _entered.TrySetResult(true);
            return new ValueTask(disposal);
        }

        public ValueTask<bool> MoveNextAsync() => new(false);
    }
}
