using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Debug;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class DebugClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task EvictLocationAsync_Should_Remove_And_Allow_Reopening_The_Owned_Location(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var locationClient = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        using var rootClient = server.CreateClient();

        var warmed = await locationClient.GetLocationAsync(cancellationToken: cancellationToken);

        await Assert.That(warmed.Status).IsEqualTo(200);
        await Assert.That(warmed.IsError).IsFalse();
        await Assert.That(warmed.ResolvedLocation.Directory).IsEqualTo(workspace.Path);
        var ownedLocation = new LocationPublicRef
        {
            Directory = warmed.ResolvedLocation.Directory,
        };

        var beforeEviction = await rootClient.Debug.ListLocationsAsync(cancellationToken: cancellationToken);

        await Assert.That(beforeEviction.Status).IsEqualTo(200);
        await Assert.That(beforeEviction.IsError).IsFalse();
        await Assert.That(Contains(beforeEviction.Locations, ownedLocation)).IsTrue();

        var eviction = await rootClient.Debug.EvictLocationAsync(
            new DebugLocationEvictRequest
            {
                Location = new LocationSelector
                {
                    Directory = ownedLocation.Directory
                },
            },
            cancellationToken: cancellationToken);

        await Assert.That(eviction.Status).IsEqualTo(204);
        await Assert.That(eviction.IsError).IsFalse();

        var afterEviction = await rootClient.Debug.ListLocationsAsync(cancellationToken: cancellationToken);

        await Assert.That(afterEviction.Status).IsEqualTo(200);
        await Assert.That(afterEviction.IsError).IsFalse();
        await Assert.That(Contains(afterEviction.Locations, ownedLocation)).IsFalse();

        var reopened = await locationClient.GetLocationAsync(cancellationToken: cancellationToken);

        await Assert.That(reopened.Status).IsEqualTo(200);
        await Assert.That(reopened.IsError).IsFalse();
        await Assert.That(reopened.ResolvedLocation.Directory).IsEqualTo(ownedLocation.Directory);

        var afterReopening = await rootClient.Debug.ListLocationsAsync(cancellationToken: cancellationToken);

        await Assert.That(afterReopening.Status).IsEqualTo(200);
        await Assert.That(afterReopening.IsError).IsFalse();
        await Assert.That(Contains(afterReopening.Locations, ownedLocation)).IsTrue();

        Console.WriteLine(
            "debug-location-live: warm=" + Number(warmed.Status) +
            " evict=" + Number(eviction.Status) +
            " reopen=" + Number(reopened.Status) +
            " directory=" + ownedLocation.Directory);
    }

    private static bool Contains(IReadOnlyList<LocationPublicRef> locations, LocationPublicRef expected) =>
        locations.Any(location =>
            string.Equals(location.Directory, expected.Directory, StringComparison.Ordinal));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
