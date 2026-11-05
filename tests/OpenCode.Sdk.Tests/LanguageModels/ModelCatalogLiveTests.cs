using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.LanguageModels;

/// <summary>
/// The model catalog's three routes on the exact-pin fixture, which the distributed-build consumer
/// leg points at the published CLI, so a published build that drops one of them fails there. An
/// isolated server has no provider of its own, so the test asserts that the three answers agree,
/// never what the catalog holds.
/// </summary>
[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ModelCatalogLiveTests(PinnedOpenCodeServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ListProvidersAsync_ListModelsAsync_And_GetDefaultAsync_Should_Agree_For_One_Location(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        var providers = await LiveReadiness.WaitAsync(
            token => client.Providers.ListProvidersAsync(cancellationToken: token),
            static _ => true, "provider catalog", cancellationToken);
        var models = await client.LanguageModels.ListModelsAsync(cancellationToken: cancellationToken);
        var selected = await client.LanguageModels.GetDefaultAsync(cancellationToken: cancellationToken);

        await Assert.That(models.Location.Directory).IsEqualTo(providers.Location.Directory);
        await Assert.That(selected.Location.Directory).IsEqualTo(providers.Location.Directory);
        var providerIds = providers.Providers.Select(static provider => provider.Id).ToHashSet(StringComparer.Ordinal);
        await Assert.That(models.Models.Where(model => !providerIds.Contains(model.ProviderId))).IsEmpty();
        List<(string, string)?> allowed =
            [null, .. models.Models.Select(static model => ((string, string)?)(model.ProviderId, model.ModelId))];
        await Assert.That(allowed).Contains(selected.Default is { } chosen ? (chosen.ProviderId, chosen.ModelId) : null);
    }
}
