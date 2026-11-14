namespace OpenCode.Sdk.Internal;

/// <summary>
/// Polls plugins before reading the default once, matching opencode's CLI one-shot wait
/// (plugin-startup.ts:6-13): 25 ms between empty answers for about five seconds.
/// </summary>
/// <param name="client">The language-model family.</param>
/// <param name="time">The clock the plugin poll's waits run on.</param>
internal sealed class DefaultModelWait(LanguageModelsClient client, TimeProvider time)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Polls plugins until non-empty, failed, or past five seconds, then reads once (plugin-startup.ts:6-13).</summary>
    /// <param name="request">The query.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The default-model response.</returns>
    [SlopwatchSuppress("SW003", "The pinned CLI ends its plugin wait on any list failure (Effect.ignore, plugin-startup.ts:6-13); the default read that follows reports any real failure.")]
    public async Task<ModelDefaultResponse> WaitAsync(ModelDefaultRequest? request, OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        if (client.PipelineOrNull is { } pipeline)
        {
            try
            {
                var plugins = new PluginsClient(pipeline);
                long? start = null;
                while ((await plugins.ListPluginsAsync(new PluginListRequest { Location = request?.Location }, requestOptions, cancellationToken).ConfigureAwait(false)).Plugins.Count == 0)
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
                // Plugin-list failures are ignored on purpose to match Effect.ignore (plugin-startup.ts:12);
                // failures of the default read that follows propagate.
            }
        }

        return await client.GetDefaultAsync(request, requestOptions, cancellationToken).ConfigureAwait(false);
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
#if NET
        Task.Delay(PollInterval, time, cancellationToken);
#else
        time.Delay(PollInterval, cancellationToken);
#endif
}
