#!/usr/bin/env -S dotnet --
#:project ./OpenCode.Sdk.Tools/OpenCode.Sdk.Tools.csproj
#:property TargetFramework=$(DefaultTargetFramework)

using var cancellation = new CancellationTokenSource();
using var interrupted = new SemaphoreSlim(0);
ConsoleCancelEventHandler interrupt = (sender, e) =>
{
    // The signal thread only records the request; the run cancels below, where a failing callback surfaces.
    e.Cancel = true;
    _ = interrupted.Release();
};
Console.CancelKeyPress += interrupt;
try
{
    var run = OpenCode.Sdk.Tools.ToolApp.RunAsync(args, cancellation.Token);
    if (await Task.WhenAny(run, interrupted.WaitAsync()).ConfigureAwait(false) != run)
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
    }

    return await run.ConfigureAwait(false);
}
finally
{
    Console.CancelKeyPress -= interrupt;
}
