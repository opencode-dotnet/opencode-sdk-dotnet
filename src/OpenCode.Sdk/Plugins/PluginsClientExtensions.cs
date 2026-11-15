using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Gives plugin activation a moment before one-shot catalog reads.</summary>
public static class PluginsClientExtensions
{
    /// <summary>Waits for plugins as opencode's CLI does before listing agents, models and integrations.</summary>
    /// <param name="client">The plugin family.</param>
    /// <param name="request">The query; the server default applies when null.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Ends the wait early.</param>
    /// <returns>A task that completes when the plugin wait ends.</returns>
    /// <remarks>
    /// Lists the location's plugins until non-empty, waiting 25 ms after each empty answer for about
    /// five seconds. The schedule limit is checked after each answer, not a deadline that interrupts
    /// requests. A failed plugin list is ignored and ends the wait early, as the CLI does. A non-empty
    /// plugin list is not a completion barrier: a catalog can still grow after this method returns.
    /// On a mocking instance built through the protected constructor, the plugin poll is skipped.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="requestOptions"/> selects NoThrow, which the wait refuses.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the plugin list or its wait.</exception>
    public static Task WaitForPluginsAsync(this PluginsClient client, PluginListRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ErrorBehaviorPolicy.RefuseNoThrow(requestOptions, "Plugin waits");

        return client.PipelineOrNull is { } pipeline
            ? new PluginWait(client, pipeline.Connection.Time).WaitAsync(request, requestOptions, cancellationToken)
            : Task.CompletedTask;
    }
}
