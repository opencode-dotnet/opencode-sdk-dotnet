using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>Waits on the language-model family.</summary>
public static class LanguageModelsClientExtensions
{
    /// <summary>Waits for plugin activation as opencode's CLI one-shot commands do, then reads the default model once.</summary>
    /// <param name="client">The language-model family.</param>
    /// <param name="request">The query; the server default applies when null.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Ends the wait early.</param>
    /// <returns>The default-model response; its <c>Default</c> is null when the server has no default model.</returns>
    /// <remarks>
    /// Lists the location's plugins until non-empty, waiting 25 ms after each empty answer for about
    /// five seconds, then reads the default model once. The schedule limit is checked after each
    /// answer, not a deadline that interrupts requests. A failed plugin list is ignored and ends the
    /// wait early, as the CLI does. Every failure of the default read propagates, including
    /// <see cref="ServiceUnavailableError"/>, with no further attempt. A non-empty plugin list is not
    /// a completion barrier: a catalog can still grow after this method returns. On a mocking
    /// instance built through the protected constructor, the plugin poll is skipped and the method reads once.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="requestOptions"/> selects NoThrow, which the wait refuses.</exception>
    /// <exception cref="OpenCodeApiException">The default read returned an API error.</exception>
    /// <exception cref="OpenCodeTransportException">The default read failed in transport.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the plugin list, its wait, or the default read.</exception>
    public static Task<ModelDefaultResponse> WaitForDefaultAsync(this LanguageModelsClient client, ModelDefaultRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ErrorBehaviorPolicy.RefuseNoThrow(requestOptions, "Default-model waits");

#pragma warning disable RS0030 // The default-model wait's composition root chooses the system clock; the plugin poll's waits receive it.
        return new DefaultModelWait(client, TimeProvider.System).WaitAsync(request, requestOptions, cancellationToken);
#pragma warning restore RS0030
    }
}
