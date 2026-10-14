using OpenCode.Sdk.Models;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Experimental;

/// <summary>
/// The V1 migration status is a union whose members all start with a JSON object and are told
/// apart by their <c>status</c> sentinel, the first of them admitting two values. A fresh
/// isolated server has no V1 history to migrate, so it answers with the idle member; the typed
/// read proves the first-match arm selects a member on a real server, not only on the corpus.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class MigrationStatusLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task GetMigrationV1StatusAsync_Should_Read_The_Idle_Member_On_A_Fresh_Server(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var response = await client.Experimental.GetMigrationV1StatusAsync(cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.MigrationV1Status.Kind).IsEqualTo(ExperimentalMigrationV1StatusKind.ExperimentalMigrationV1StatusIdle);
        await Assert
            .That(response.MigrationV1Status.ExperimentalMigrationV1StatusIdle.Status)
            .IsIn(ExperimentalMigrationV1StatusIdleStatus.Required, ExperimentalMigrationV1StatusIdleStatus.Completed);
    }
}
