using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>Waits on the language-model family.</summary>
public static class LanguageModelsClientExtensions
{
    /// <summary>Gets the default model, asking again while the location's plugins activate.</summary>
    /// <param name="client">The language-model family.</param>
    /// <param name="request">The query; the server default applies when null.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Ends the wait early.</param>
    /// <returns>The default-model response; its <c>Default</c> is null when the server has no default model.</returns>
    /// <remarks>
    /// While the location's plugins activate, the server holds the request up to five seconds and,
    /// if activation has not finished, answers <see cref="ServiceUnavailableError"/>. On that answer
    /// this method asks again on the schedule opencode's app uses for its boot-time catalog sync:
    /// up to twelve attempts, waiting one second after the first and doubling to at most five seconds.
    /// The last unavailable answer surfaces, and so does every other failure. Plugin activation has
    /// no completion barrier, and a catalog can still grow after this method returns.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="requestOptions"/> selects NoThrow, which the wait refuses.</exception>
    /// <exception cref="OpenCodeApiException">The API returned an error other than a retried unavailable answer, or the attempts ran out.</exception>
    /// <exception cref="OpenCodeTransportException">The request failed in transport.</exception>
    public static Task<ModelDefaultResponse> WaitForDefaultAsync(this LanguageModelsClient client, ModelDefaultRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ErrorBehaviorPolicy.RefuseNoThrow(requestOptions, "Default-model waits");

#pragma warning disable RS0030 // The default-model wait's composition root chooses the system clock; its waits between attempts receive it.
        return new DefaultModelWait(client, TimeProvider.System).WaitAsync(request, requestOptions, cancellationToken);
#pragma warning restore RS0030
    }
}
