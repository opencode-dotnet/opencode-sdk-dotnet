using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Asks for the default model until the location's plugins have activated, on the schedule upstream's
/// app uses for its boot-time catalog sync: up to twelve attempts, waiting one second after the first
/// unavailable answer and doubling to at most five seconds.
/// </summary>
/// <param name="client">The language-model family.</param>
/// <param name="time">The clock the waits between attempts run on.</param>
internal sealed class DefaultModelWait(LanguageModelsClient client, TimeProvider time)
{
    private const int Attempts = 12;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>Asks again after each unavailable answer, until the attempts run out.</summary>
    /// <param name="request">The query.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The default-model response.</returns>
    public async Task<ModelDefaultResponse> WaitAsync(ModelDefaultRequest? request, OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt < Attempts; attempt++)
        {
            try
            {
                return await client.GetDefaultAsync(request, requestOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (OpenCodeApiException failure) when (failure.Error is ServiceUnavailableError)
            {
                await DelayAsync(DelayAfter(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        return await client.GetDefaultAsync(request, requestOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The wait after an attempt: one second, doubling, at most five seconds.</summary>
    /// <param name="attempt">The attempt that was just answered, from 1 to eleven.</param>
    /// <returns>How long to wait before the next attempt.</returns>
    private static TimeSpan DelayAfter(int attempt) =>
        TimeSpan.FromTicks(Math.Min(FirstDelay.Ticks << (attempt - 1), MaxDelay.Ticks));

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
#if NET
        Task.Delay(delay, time, cancellationToken);
#else
        time.Delay(delay, cancellationToken);
#endif
}
