using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The Windows pump: one <see cref="ChildOutputReader"/> thread per stream, so the synchronous
/// pipe reads hold no thread-pool thread. Each reader thread closes its pipe's read handle as it
/// ends, because <see cref="Process"/> leaves a synchronously read stream open. The .NET 11
/// <see cref="Process"/> opens the parent's read ends overlapped instead (dotnet/runtime#125643),
/// which no current target of this SDK has.
/// </summary>
internal sealed class DedicatedThreadOutputPump : ChildOutputPump
{
    /// <summary>How often a release repeats the cancel while a reader is still blocked.</summary>
    private static readonly TimeSpan CancelRetryInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// How long a release keeps canceling. A canceled read ends at once, so this only bounds the
    /// case where the platform refuses the cancel; the release then returns with that reader still
    /// waiting for end-of-stream, because a release never hangs its caller.
    /// </summary>
    private static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(5);

    private readonly ChildOutputReader _standardOutput;
    private readonly ChildOutputReader _standardError;

    private DedicatedThreadOutputPump(ChildOutputReader standardOutput, ChildOutputReader standardError)
    {
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    /// <summary>Starts one reader thread for each redirected stream.</summary>
    /// <param name="process">The started child.</param>
    /// <param name="onStandardOutput">Receives each stdout line.</param>
    /// <param name="onStandardError">Receives each stderr line.</param>
    /// <returns>The running pump.</returns>
    public static DedicatedThreadOutputPump Begin(Process process, Action<string> onStandardOutput, Action<string> onStandardError) =>
        new(
            ChildOutputReader.Start(process.StandardOutput, onStandardOutput, "opencode-server-stdout"),
            ChildOutputReader.Start(process.StandardError, onStandardError, "opencode-server-stderr"));

    /// <inheritdoc />
    public override Task ReadersEnded => Task.WhenAll(_standardOutput.Completion, _standardError.Completion);

    /// <inheritdoc />
    public override async Task<bool> DrainAsync(TimeSpan bound)
    {
        try
        {
            var endOfStream = await Task.WhenAll(_standardOutput.Completion, _standardError.Completion)
                .WaitAsync(bound)
                .ConfigureAwait(false);
            return endOfStream[0] && endOfStream[1];
        }
        catch (TimeoutException)
        {
            // End-of-stream did not arrive inside the bound; the release that follows ends the readers.
            return false;
        }
    }

    /// <inheritdoc />
    public override async Task ReleaseAsync()
    {
        var ended = ReadersEnded;
        var elapsed = Stopwatch.StartNew();
        while (!ended.IsCompleted && elapsed.Elapsed < ReleaseBound)
        {
            _standardOutput.CancelPendingRead();
            _standardError.CancelPendingRead();
            _ = await Task.WhenAny(ended, Task.Delay(CancelRetryInterval)).ConfigureAwait(false);
        }
    }
}
