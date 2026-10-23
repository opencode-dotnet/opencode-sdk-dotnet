using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// An <see cref="IChildExitStatus"/> a test drives without a process: the blocked
/// <see cref="WaitUntilExited"/> returns once the test calls <see cref="Exit"/>, and the reap then
/// answers the status the test gave. <see cref="Stop"/> ends the wait the way macOS ends it when the
/// child only stops: the wait reports the child, and the reap finds no exit until <see cref="Exit"/>.
/// The test calls <see cref="Exit"/> or <see cref="Release"/> on every path, so no watch thread
/// outlives it.
/// </summary>
internal sealed class ScriptedChildExit : IChildExitStatus, IDisposable
{
    private readonly ManualResetEventSlim _exited = new();
    private readonly bool _reapedAutomatically;
    private ChildExitStatus? _status;
    private bool _stopped;
    private int _waits;

    public ScriptedChildExit(bool reapedAutomatically = false) => _reapedAutomatically = reapedAutomatically;

    /// <summary>Gets how many times the blocking wait was entered.</summary>
    public int Waits => Volatile.Read(ref _waits);

    /// <summary>Gets or sets the exception the blocking wait throws once released; null returns normally.</summary>
    public Exception? WaitFailure { get; set; }

    /// <summary>Lets the child exit with a status the reap then answers.</summary>
    /// <param name="status">The status.</param>
    public void Exit(ChildExitStatus status)
    {
        Volatile.Write(ref _status, status);
        _exited.Set();
    }

    /// <summary>Releases a blocked wait as reporting a child that has not exited, as macOS reports a stop.</summary>
    public void Stop()
    {
        Volatile.Write(ref _stopped, true);
        _exited.Set();
    }

    /// <summary>Releases a blocked wait without an exit, as a failed wait would return.</summary>
    public void Release() => _exited.Set();

    public bool AreChildrenReapedAutomatically() => _reapedAutomatically;

    public bool WaitUntilExited(int processId)
    {
        _ = Interlocked.Increment(ref _waits);
        _exited.Wait();
        return WaitFailure is { } failure ? throw failure : Volatile.Read(ref _status) is not null || Volatile.Read(ref _stopped);
    }

    public ChildExitStatus? Reap(int processId) => Volatile.Read(ref _status);

    public bool IsUnreapedChild(int processId) => throw new InvalidOperationException("IsUnreapedChild was not scripted.");

    public void Dispose()
    {
        _exited.Set();
        _exited.Dispose();
    }
}
