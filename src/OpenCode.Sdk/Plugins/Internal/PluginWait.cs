namespace OpenCode.Sdk.Internal;

/// <summary>Gives plugin activation a moment before a one-shot catalog read, matching the pinned CLI.</summary>
/// <param name="client">The plugin family.</param>
/// <param name="time">The clock the plugin poll's waits run on.</param>
internal sealed class PluginWait(PluginsClient client, TimeProvider time)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Polls plugins until non-empty, failed, or past five seconds (plugin-startup.ts:6-13).</summary>
    /// <param name="request">The query.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>A task that completes when the poll ends.</returns>
    [SlopwatchSuppress("SW003", "The pinned CLI ends its plugin wait on any list failure and reports nothing (Effect.ignore, plugin-startup.ts:6-13); the caller's next read surfaces a real failure.")]
    public async Task WaitAsync(PluginListRequest? request, OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        try
        {
            long? start = null;
            while ((await client.ListPluginsAsync(request, requestOptions, cancellationToken).ConfigureAwait(false)).Plugins.Count == 0)
            {
                start ??= time.GetTimestamp();
                if (time.GetElapsedTime(start.Value) > TimeSpan.FromSeconds(5))
                {
                    break;
                }

                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OpenCodeException)
        {
            // Plugin-list failures end the wait on purpose, as Effect.ignore does (plugin-startup.ts:12).
        }
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
#if NET
        Task.Delay(PollInterval, time, cancellationToken);
#else
        time.Delay(PollInterval, cancellationToken);
#endif
}
