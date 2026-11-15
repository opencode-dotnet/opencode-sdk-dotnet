namespace OpenCode.Sdk.Internal;

/// <summary>
/// Polls plugins before reading the default once, matching opencode's CLI one-shot wait
/// (plugin-startup.ts:6-13): 25 ms between empty answers for about five seconds.
/// </summary>
/// <param name="client">The language-model family.</param>
/// <param name="time">The clock the plugin poll's waits run on.</param>
internal sealed class DefaultModelWait(LanguageModelsClient client, TimeProvider time)
{
    /// <summary>Polls plugins until non-empty, failed, or past five seconds, then reads once (plugin-startup.ts:6-13).</summary>
    /// <param name="request">The query.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The default-model response.</returns>
    public async Task<ModelDefaultResponse> WaitAsync(ModelDefaultRequest? request, OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        if (client.PipelineOrNull is { } pipeline)
        {
            await new PluginWait(new PluginsClient(pipeline), time).WaitAsync(
                new PluginListRequest { Location = request?.Location }, requestOptions, cancellationToken).ConfigureAwait(false);
        }

        return await client.GetDefaultAsync(request, requestOptions, cancellationToken).ConfigureAwait(false);
    }
}
