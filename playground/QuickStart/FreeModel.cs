using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.QuickStart;

/// <summary>Waits out the server's startup to get a free model. You can skip this file on a first read.</summary>
/// <param name="client">The client.</param>
internal sealed class FreeModel(OpenCodeClient client)
{
    /// <summary>Finds a free model in the live catalog, since the free list changes.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A free model, or null when none appeared within 30 seconds.</returns>
    public async Task<ModelInfo?> FindAsync(CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var free = await ReadAsync(startup.Token);
                if (free is not null)
                {
                    return free;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), startup.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<ModelInfo?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.LanguageModels.GetDefaultAsync(cancellationToken: cancellationToken);
            var catalog = await client.LanguageModels.ListModelsAsync(cancellationToken: cancellationToken);
            return catalog.Models.FirstOrDefault(static model => model.ProviderId == "opencode" && model.Cost.All(static cost => cost.Input <= 0));
        }
        catch (OpenCodeApiException failure) when (failure.Error is ServiceUnavailableError)
        {
            // A new server is still loading its plugins: try again.
            return null;
        }
    }
}
