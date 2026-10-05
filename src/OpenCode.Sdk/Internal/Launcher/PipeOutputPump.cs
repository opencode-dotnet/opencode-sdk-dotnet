namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// Reads a POSIX child's stdout and stderr continuously, one <see cref="PipeLineReader"/> per
/// pipe, and releases the readers. The launcher creates these pipes itself, and their reads are
/// asynchronous and hold no thread while they wait. Standard error has no reader when the host's
/// own descriptor was handed to the child instead of a pipe.
/// </summary>
internal sealed class PipeOutputPump
{
    /// <summary>
    /// How long a release waits for its readers. A released read ends at once; the bound only
    /// covers a runtime whose pipe read neither the token nor the closed stream reaches, and a
    /// release never hangs its caller.
    /// </summary>
    private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(5);

    private readonly PipeLineReader _standardOutput;
    private readonly PipeLineReader? _standardError;

    private PipeOutputPump(
        Stream standardOutput, Stream? standardError, Action<string> onStandardOutput, Action<string> onStandardError)
    {
        _standardOutput = PipeLineReader.Start(standardOutput, onStandardOutput);
        _standardError = standardError is null ? null : PipeLineReader.Start(standardError, onStandardError);
    }

    /// <summary>Gets a task that completes once standard output has ended, at end-of-stream or on a failed read.</summary>
    public Task StandardOutputEnded => _standardOutput.Completion;

    /// <summary>Gets a task that completes once no reader of this pump is pending any more.</summary>
    public Task ReadersEnded =>
        _standardError is null ? _standardOutput.Completion : Task.WhenAll(_standardOutput.Completion, _standardError.Completion);

    /// <summary>Starts a reader on each pipe.</summary>
    /// <param name="standardOutput">The read end of the child's standard output.</param>
    /// <param name="standardError">The read end of the child's standard error; null when it was not routed to a pipe.</param>
    /// <param name="onStandardOutput">Receives each stdout line.</param>
    /// <param name="onStandardError">Receives each stderr line.</param>
    /// <returns>The running pump.</returns>
    public static PipeOutputPump Start(
        Stream standardOutput, Stream? standardError, Action<string> onStandardOutput, Action<string> onStandardError) =>
        new(standardOutput, standardError, onStandardOutput, onStandardError);

    /// <summary>
    /// Waits, inside <paramref name="bound"/>, for every pipe to reach end-of-stream, so every line
    /// the child wrote has been delivered. A descendant holding a pipe open can prevent that: the
    /// bound is the guarantee, and an expired bound is reported rather than raised.
    /// </summary>
    /// <param name="bound">How long the caller waits for end-of-stream.</param>
    /// <returns>True when every pipe reached end-of-stream inside the bound.</returns>
    public async Task<bool> DrainAsync(TimeSpan bound)
    {
        if (!await BoundedWait.CompletesWithinAsync(ReadersEnded, bound).ConfigureAwait(false))
        {
            return false;
        }

        return (_standardError is null || await ReachedEndAsync(_standardError).ConfigureAwait(false)) &&
               await ReachedEndAsync(_standardOutput).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends every reader, whether or not its pipe reached end-of-stream, and closes the pipes.
    /// After it completes no reader is pending.
    /// </summary>
    /// <returns>A task that completes once the readers are released.</returns>
    public async Task ReleaseAsync()
    {
        await _standardOutput.DisposeAsync().ConfigureAwait(false);
        if (_standardError is not null)
        {
            await _standardError.DisposeAsync().ConfigureAwait(false);
        }

        _ = await BoundedWait.CompletesWithinAsync(ReadersEnded, ReleaseBound).ConfigureAwait(false);
    }

    /// <summary>Whether an ended reader reached end-of-stream; one whose line handler failed did not.</summary>
    private static async Task<bool> ReachedEndAsync(PipeLineReader reader) =>
        reader.Completion.Status is TaskStatus.RanToCompletion && await reader.Completion.ConfigureAwait(false);
}
