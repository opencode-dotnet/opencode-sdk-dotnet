namespace OpenCode.Sdk.Tests.Support;

/// <summary>Starts synchronous test work that must not hold a thread-pool thread.</summary>
internal static class DedicatedThread
{
    public static Task<T> Run<T>(Func<T> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
