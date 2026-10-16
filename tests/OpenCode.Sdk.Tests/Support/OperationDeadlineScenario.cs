using System.Collections.Concurrent;
using NSubstitute;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class OperationDeadlineScenario
{
    private readonly Dictionary<string, ControlledOperationDeadline> _deadlines = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Task> _operations = new();

    public OperationDeadlineScenario()
    {
        Deadline = Substitute.For<IOwnedOperationDeadline>();
        Deadline.WaitAsync(Arg.Any<string>(), Arg.Any<Task>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => WaitAsync(call.ArgAt<string>(0), call.ArgAt<Task>(1), call.ArgAt<CancellationToken>(3)));
    }

    public IOwnedOperationDeadline Deadline { get; }

    public ControlledOperationDeadline Hold(string name)
    {
        var deadline = new ControlledOperationDeadline();
        _deadlines.Add(name, deadline);
        return deadline;
    }

    public async Task DrainAsync(Task completion)
    {
        foreach (var deadline in _deadlines.Values)
        {
            deadline.Release();
        }

        await ObserveAsync(completion);
        await ObserveAsync(Task.WhenAll(_operations));
    }

    private async Task WaitAsync(string name, Task operation, CancellationToken cancellationToken)
    {
        _operations.Enqueue(operation);
        if (_deadlines.TryGetValue(name, out var deadline))
        {
            await deadline.WaitAsync(operation);
            return;
        }

        await ObserveAsync(operation).WaitAsync(cancellationToken);
    }

    private static Task ObserveAsync(Task operation) => operation.ContinueWith(
        static completed => { _ = completed.Exception; }, CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
