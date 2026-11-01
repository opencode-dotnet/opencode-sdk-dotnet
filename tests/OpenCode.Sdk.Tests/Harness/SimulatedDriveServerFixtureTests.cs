using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Harness;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SimulatedDriveServerFixtureTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task OnTestEnd_Should_Evict_The_Locations_A_Test_Loaded(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        using var rootClient = server.CreateClient();
        var directory = (await client.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation.Directory;
        var before = await rootClient.Debug.ListLocationsAsync(cancellationToken: cancellationToken);
        await Assert.That(before.Locations.Select(location => location.Directory)).Contains(directory, StringComparer.Ordinal);
        await server.OnTestEnd(TestContext.Current!);
        var after = await rootClient.Debug.ListLocationsAsync(cancellationToken: cancellationToken);
        await Assert.That(after.Locations.Select(location => location.Directory)).DoesNotContain(directory, StringComparer.Ordinal);
    }

    [Test]
    [Timeout(60_000)]
    public async Task EvictFinishedLocationsAsync_Should_Leave_Only_The_Default_Location(CancellationToken cancellationToken)
    {
        using var first = server.CreateWorkspace();
        using var second = server.CreateWorkspace();
        using var firstClient = server.CreateClient(new LocationSelector { Directory = first.Path });
        using var secondClient = server.CreateClient(new LocationSelector { Directory = second.Path });
        using var rootClient = server.CreateClient();
        var firstLocation = (await firstClient.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation.Directory;
        var secondLocation = (await secondClient.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation.Directory;
        var defaultLocation = (await rootClient.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation.Directory;
        var before = await rootClient.Debug.ListLocationsAsync(cancellationToken: cancellationToken);
        await Assert.That(before.Locations.Select(location => location.Directory)).Contains(firstLocation, StringComparer.Ordinal);
        await Assert.That(before.Locations.Select(location => location.Directory)).Contains(secondLocation, StringComparer.Ordinal);
        await server.EvictFinishedLocationsAsync(cancellationToken);
        var after = await rootClient.Debug.ListLocationsAsync(cancellationToken: cancellationToken);
        await Assert.That(after.Locations.Select(location => location.Directory)).DoesNotContain(firstLocation, StringComparer.Ordinal);
        await Assert.That(after.Locations.Select(location => location.Directory)).DoesNotContain(secondLocation, StringComparer.Ordinal);
        await Assert.That(after.Locations.Select(location => location.Directory)).Contains(defaultLocation, StringComparer.Ordinal);
    }

    [Test]
    [Timeout(60_000)]
    public async Task A_Location_Should_Load_Again_After_Its_Eviction(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Location = new LocationPublicRef { Directory = workspace.Path },
        }, cancellationToken: cancellationToken);
        var directory = (await client.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation.Directory;
        await server.EvictFinishedLocationsAsync(cancellationToken);
        var evicted = await client.Debug.ListLocationsAsync(cancellationToken: cancellationToken);
        await Assert.That(evicted.Locations.Select(location => location.Directory)).DoesNotContain(directory, StringComparer.Ordinal);
        var read = await client.Sessions.GetSessionClient(created.Session.Id).GetAsync(cancellationToken: cancellationToken);
        await Assert.That(read.Session.Id).IsEqualTo(created.Session.Id);
        _ = await client.GetLocationAsync(cancellationToken: cancellationToken);
        var reloaded = await client.Debug.ListLocationsAsync(cancellationToken: cancellationToken);
        await Assert.That(reloaded.Locations.Select(location => location.Directory)).Contains(directory, StringComparer.Ordinal);
        _ = await client.Sessions.GetSessionClient(created.Session.Id).RemoveAsync(cancellationToken: cancellationToken);
    }
}
