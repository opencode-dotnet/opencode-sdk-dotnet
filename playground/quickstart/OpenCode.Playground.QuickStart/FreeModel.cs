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
        // This budget can end the default-model wait before its plugin poll ends, ending the search with no model.
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

    /// <summary>Gets whether a model is free: one of opencode's own, with a price list whose every tier charges nothing for input and output.</summary>
    /// <param name="model">The model.</param>
    /// <returns>True when the model is free.</returns>
    internal static bool IsFree(ModelInfo model) =>
        model.ProviderId == "opencode" && model.Cost.Count > 0 && model.Cost.All(static cost => cost.Input <= 0 && cost.Output <= 0);

    private async Task<ModelInfo?> ReadAsync(CancellationToken cancellationToken)
    {
        await client.LanguageModels.WaitForDefaultAsync(cancellationToken: cancellationToken);

        // Plugins load in the background, so the catalog can still be partial: the next round reads it again.
        var catalog = await client.LanguageModels.ListModelsAsync(cancellationToken: cancellationToken);
        return catalog.Models.FirstOrDefault(IsFree);
    }
}
