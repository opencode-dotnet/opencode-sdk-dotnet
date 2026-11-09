namespace OpenCode.Sdk.Tests.Support;

/// <summary>Starts test work on its own thread; only the code before the body's first incomplete await stays there.</summary>
internal static class DedicatedThread
{
    public static Task Run(Action body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task Run(Action body, out Thread thread)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                body();
                completion.SetResult(true);
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return completion.Task;
    }

    public static Task<T> Run<T>(Func<T> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
