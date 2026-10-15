using System.Globalization;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Sandbox;

/// <summary>
/// The compile-and-run home for the getting-started guide's "choosing a model" recipe: read the
/// catalog the
/// location actually has, and place the chosen model on session creation. Runs on the
/// launcher-started leg, where status has just answered — which is exactly the moment the catalog
/// can still be empty.
/// </summary>
internal static class ModelSelectionWalkthrough
{
    public static async Task RunAsync(OpenCodeClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        // The guide's one readiness step comes first; the catalogs after it are still snapshots.
        await WaitForPluginsAsync(client).ConfigureAwait(false);
        var providers = await client.Providers.ListProvidersAsync().ConfigureAwait(false);
        var models = await client.LanguageModels.ListModelsAsync().ConfigureAwait(false);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"catalog: providers={providers.Providers.Count} models={models.Models.Count}"));

        // Listing already filters to enabled models of available providers, so this is a guard
        // against an empty catalog rather than a filter of its own.
        var model = models.Models.FirstOrDefault(static candidate => candidate.Enabled);
        if (model is null)
        {
            // Provider inventory is the machine's ambient opencode configuration: a host with no
            // provider credentials can have nothing to choose from. Plugin activation may also still be in progress.
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"model: none currently available on this host ({providers.Providers.Count} providers); session stays default"));
            return;
        }

        Console.WriteLine(
            $"model: provider={model.ProviderId} id={model.Id} modelId={model.ModelId} name={model.Name}");

        // A session reference carries the catalog id, ModelInfo.Id — never ModelInfo.ModelId, which
        // is the id the provider's own API uses.
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "sdk model-selection demo",
            Model = new ModelRef { ProviderId = model.ProviderId, Id = model.Id },
        }).ConfigureAwait(false);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"session: status={created.Status} id={created.Session.Id}"));

        var removed = await client.Sessions.GetSessionClient(created.Session.Id)
            .RemoveAsync(OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cleanup: status={removed.Status}"));
    }

    /// <summary>
    /// The guide's one readiness step: the default model answers a declared 503 until the location's
    /// plugins have loaded, which is "still loading", not a failure; once it answers, the model
    /// calls are ready.
    /// </summary>
    [SlopwatchSuppress(
        "SW004",
        "The guide's documented startup wait for another process: the default-model call answers a declared 503 until the location's plugins have loaded, the server sends no signal this recipe can await, and the wait is bounded by a thirty-second deadline.")]
    private static async Task WaitForPluginsAsync(OpenCodeClient client)
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                _ = await client.LanguageModels.GetDefaultAsync(cancellationToken: startup.Token).ConfigureAwait(false);
                return;
            }
            catch (OpenCodeApiException failure) when (failure.Error is ServiceUnavailableError)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), startup.Token).ConfigureAwait(false);
            }
        }
    }
}
