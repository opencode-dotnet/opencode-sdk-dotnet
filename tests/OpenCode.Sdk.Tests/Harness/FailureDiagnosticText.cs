using System.Collections.Concurrent;
using System.Text;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Bounds each failure summary without serializing arbitrary exception data.</summary>
internal sealed class FailureDiagnosticText(int maximumCharacters = 16_384)
{
    internal const int MaximumCharacters = 16_384;
    private const string Truncated = "\n[truncated]";

    public string Bound(string text) => text.Length <= maximumCharacters
        ? text
        : text[..(maximumCharacters - Truncated.Length)] + Truncated;

    public string Describe(Exception exception)
    {
        var text = new StringBuilder();
        var pending = new Queue<Exception>();
        var seen = new HashSet<Exception>();
        pending.Enqueue(exception);
        while (pending.Count > 0 && text.Length < MaximumCharacters && seen.Count < 64)
        {
            var current = pending.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            _ = text.AppendLine(current.GetType().FullName).AppendLine(new FailureDiagnosticText(1_024).Bound(current.Message));
            if (current.Data[EventDiagnosticSummary.DataKey] is string events)
            {
                _ = text.AppendLine(EventDiagnosticSummary.DataKey).AppendLine(Bound(events));
            }

            if (current.Data[OwnedCleanup.StepTimingsKey] is string timings)
            {
                _ = text.AppendLine(OwnedCleanup.StepTimingsKey).AppendLine(Bound(timings));
            }

            if (current.InnerException is { } inner)
            {
                pending.Enqueue(inner);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var failure in aggregate.InnerExceptions.Take(64))
                {
                    pending.Enqueue(failure);
                }

                if (aggregate.InnerExceptions.Count > 64)
                {
                    _ = text.Append(Truncated);
                }
            }

            if (current.Data[OwnedCleanup.FailuresKey] is AggregateException cleanup)
            {
                pending.Enqueue(cleanup);
            }

            if (current.Data[GitRepositoryWorkspace.CleanupFailuresKey] is AggregateException repositoryCleanup)
            {
                _ = text.AppendLine(GitRepositoryWorkspace.CleanupFailuresKey);
                pending.Enqueue(repositoryCleanup);
            }

            if (current.Data[OwnedCleanup.LateFailuresKey] is ConcurrentQueue<KeyValuePair<string, Exception>> late)
            {
                foreach (var entry in late.Take(64))
                {
                    _ = text.AppendLine(Bound(entry.Key));
                    pending.Enqueue(entry.Value);
                }
            }
        }

        if (pending.Count > 0)
        {
            _ = text.Append(Truncated);
        }

        return Bound(text.ToString());
    }
}
