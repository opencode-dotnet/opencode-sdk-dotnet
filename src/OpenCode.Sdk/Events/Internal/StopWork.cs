namespace OpenCode.Sdk.Internal;

/// <summary>The detached generation whose cancellation must run only after the feed gate is released.</summary>
internal sealed class StopWork(EventSource source)
{
    public void Run() => source.Dispose();
}
