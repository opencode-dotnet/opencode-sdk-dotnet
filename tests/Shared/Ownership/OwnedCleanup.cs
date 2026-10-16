using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Harness.Ownership;

internal sealed class OwnedCleanup(TimeSpan timeout, IOwnedOperationDeadline deadline)
{
    internal const string FailuresKey = "OwnedSessionCleanup.Failures";
    internal const string LateFailuresKey = "OwnedSessionCleanup.LateFailures";

    /// <summary>
    /// The per-step timeline of a cleanup that failed: each step's name and how long it took, or
    /// the budget it exceeded, in the order the steps ran.
    /// </summary>
    internal const string StepTimingsKey = "OwnedCleanup.StepTimings";

    private readonly LateCleanupFailureReport _lateFailures = new();
    private readonly List<OwnedCleanupOperation> _operations = [];
    private readonly List<string> _timeline = [];

    public OwnedCleanup(TimeSpan timeout) : this(timeout, new OwnedOperationDeadline())
    {
    }

    public LateCleanupFailureReport? LateFailures =>
        _lateFailures.HasRegistrations ? _lateFailures : null;

    /// <summary>
    /// A copied snapshot of immediate operation failures from the completed observation pass,
    /// before primary deduplication. Late failures remain in their existing owned report.
    /// </summary>
    public IReadOnlyList<Exception> OperationFailures { get; private set; } = [];

    public void Own(string name, Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(new OwnedCleanupOperation(name, operation));
    }

    /// <summary>
    /// Owns an operation under its own budget instead of the shared one, for a step whose inner
    /// bound is longer than the shared budget: the step's budget then stays above that inner bound,
    /// so the inner bound's named failure is what a stuck step reports.
    /// </summary>
    public void Own(string name, TimeSpan timeout, Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(new OwnedCleanupOperation(name, operation, timeout));
    }

    public void Own(string name, Task pending, Func<OperationCanceledException, bool>? expectedCancellation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pending);
        _operations.Add(new OwnedCleanupOperation(name, pending, expectedCancellation));
    }

    public async Task CompleteAsync(Exception? primaryFailure)
    {
        _lateFailures.InheritDiagnostics(primaryFailure);
        var failures = new List<Exception>();
        foreach (var operation in _operations)
        {
            await CaptureFailureAsync(operation, failures);
        }

        OperationFailures = [.. failures];
        failures.RemoveAll(failure => ReferenceEquals(failure, primaryFailure));
        ThrowFailures(primaryFailure, failures);
    }

    private async Task CaptureFailureAsync(
        OwnedCleanupOperation operation,
        List<Exception> failures)
    {
        var stepTimeout = operation.Timeout ?? timeout;
        var started = Stopwatch.GetTimestamp();
        CancellationTokenSource? budget = new();
        try
        {
            var token = budget.Token;
            // Invocation itself can block (including a synchronous cancellation callback).
            // The deadline must not depend on that work or on its cooperative token.
            var pending = operation.StartAsync(token);
            var exceeded = await ObserveAsync(
                operation.Name, pending, stepTimeout, failures, operation.ExpectedCancellation);
            _timeline.Add(exceeded
                ? $"'{operation.Name}' exceeded {stepTimeout}"
                : $"'{operation.Name}' {Seconds(started)}");
            if (pending.IsCompleted)
            {
                return;
            }

            // The owned worker captures callback faults without a nested downlevel CancelAsync work item.
            var cancellation = Task.Run(budget.Cancel, CancellationToken.None);
            _ = await ObserveAsync(operation.Name + " cancellation", cancellation, stepTimeout, failures);
            DisposeBudgetAfterCompletion(budget, pending, cancellation);
            budget = null;
        }
        finally
        {
            budget?.Dispose();
        }
    }

    private static void DisposeBudgetAfterCompletion(CancellationTokenSource budget, Task pending, Task cancellation)
    {
        _ = Task.WhenAll(pending, cancellation).ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                budget.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string Seconds(long started) =>
        ((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency)
        .ToString("F2", CultureInfo.InvariantCulture) + " s";

    /// <summary>Observes one operation under its budget; answers whether the budget expired first.</summary>
    private async Task<bool> ObserveAsync(string name, Task pending, TimeSpan budget, List<Exception> failures,
        Func<OperationCanceledException, bool>? expectedCancellation = null)
    {
        try
        {
            await deadline.WaitAsync(name, pending, budget);
        }
        catch (TimeoutException exception)
        {
            var before = _timeline.Count > 0 ? $" Steps before it: {string.Join(", ", _timeline)}." : string.Empty;
            failures.Add(new TimeoutException(
                $"Owned operation '{name}' exceeded its cleanup deadline of {budget}.{before}", exception));
            // The task may already be terminal when the deadline result reaches us.
            // Always observe it once, including cancellation with no Task.Exception.
            _lateFailures.Observe(name, pending, expectedCancellation);
            return true;
        }

        try
        {
            await pending;
        }
        catch (OperationCanceledException exception) when (expectedCancellation?.Invoke(exception) is true)
        {
            // An owned reader can identify its own cooperative teardown cancellation.
            return false;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            if (pending.Exception is { } aggregate)
            {
                foreach (var failure in aggregate.Flatten().InnerExceptions)
                {
                    if (!ReferenceEquals(failure, exception)
                        && (exception is not AggregateException caught
                            || !caught.Flatten().InnerExceptions.Contains(failure)))
                    {
                        failures.Add(failure);
                    }
                }
            }
        }

        return false;
    }

    private void ThrowFailures(Exception? primaryFailure, List<Exception> failures)
    {
        if (primaryFailure is not null)
        {
            if (failures.Count > 0)
            {
                var previous = primaryFailure.Data[FailuresKey] as AggregateException;
                primaryFailure.Data[FailuresKey] = new AggregateException(
                    (previous?.InnerExceptions.AsEnumerable() ?? []).Concat(failures));
                AttachTimeline(primaryFailure);
            }

            AttachLateFailures(primaryFailure);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (failures.Count is 1)
        {
            AttachTimeline(failures[0]);
            AttachLateFailures(failures[0]);
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            var aggregate = new AggregateException("Multiple failures occurred during owned cleanup.", failures);
            AttachTimeline(aggregate);
            AttachLateFailures(aggregate);
            throw aggregate;
        }
    }

    private void AttachTimeline(Exception exception) =>
        exception.Data[StepTimingsKey] = string.Join("; ", _timeline);

    private void AttachLateFailures(Exception exception)
    {
        if (_lateFailures.HasRegistrations)
        {
            exception.Data[LateFailuresKey] = _lateFailures.DiagnosticFailures;
        }
    }
}
