using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// A caller-owned collector of a started server's stdout and stderr, supplied through
/// <see cref="OpenCodeServerOptions.Output"/>. It retains a bounded tail of each stream — the
/// newest 500 lines and at most 1,048,576 characters per stream, the first stdout line
/// (the readiness contract) included — for pull snapshots, and it invokes no caller code from
/// the process readers. One collector binds to exactly one start attempt, before the child is
/// spawned; it stays readable after a failed start, and it needs no disposal. Once the owning
/// server has been disposed (or the start has failed) the collection is closed and later
/// arrivals are ignored, so a snapshot taken then is final. On Linux and macOS the collection
/// closes when disposal starts, before the server is asked to stop, so what the server writes while
/// it shuts down is not collected; on Windows disposal drains the output, within a bound, before
/// the collection closes. On Linux and macOS, with <c>OPENCODE_PRINT_LOGS=1</c> in the host's
/// environment, the server writes its stderr to the host's own stderr, and no stderr is collected.
/// </summary>
public sealed class OpenCodeServerOutput
{
    /// <summary>The newest lines each stream retains.</summary>
    internal const int RetainedLines = 500;

    /// <summary>The most content each stream retains, in UTF-16 characters, line terminators excluded.</summary>
    internal const int RetainedCharacters = 1_048_576;

    private readonly Lock _gate = new();
    private readonly BoundedLineBuffer _standardOutput = new(RetainedLines, RetainedCharacters);
    private readonly BoundedLineBuffer _standardError = new(RetainedLines, RetainedCharacters);
    private int _bound;
    private bool _completed;

    /// <summary>
    /// Copies the retained tail of both streams. The snapshot is stable: output arriving later
    /// never mutates it.
    /// </summary>
    /// <returns>The retained lines of each stream, oldest first, and whether either stream lost content.</returns>
    public OpenCodeServerOutputSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new OpenCodeServerOutputSnapshot
            {
                StandardOutput = _standardOutput.Snapshot(),
                StandardError = _standardError.Snapshot(),
                StandardOutputTruncated = _standardOutput.Truncated,
                StandardErrorTruncated = _standardError.Truncated,
            };
        }
    }

    /// <summary>Claims this collector for one start attempt.</summary>
    /// <returns>True on the first claim; false when an earlier start already owns the collector.</returns>
    internal bool TryBind() => Interlocked.Exchange(ref _bound, 1) is 0;

    internal void AppendStandardOutput(string line)
    {
        lock (_gate)
        {
            if (!_completed)
            {
                _standardOutput.Append(line);
            }
        }
    }

    internal void AppendStandardError(string line)
    {
        lock (_gate)
        {
            if (!_completed)
            {
                _standardError.Append(line);
            }
        }
    }

    /// <summary>Closes the collection: everything retained so far is final, later arrivals are ignored.</summary>
    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
        }
    }
}
