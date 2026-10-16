namespace OpenCode.Sdk.Tests.Harness;

[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PinnedServerFixtureTests(PinnedOpenCodeServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task Fixture_Should_Answer_Status_Through_Its_Own_Client(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var status = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

        // The answering process names itself: an owned command override answers from behind a
        // shim and an external endpoint from another machine, so the pid is only known to be real.
        await Assert.That(status.Status).IsEqualTo(200);
        await Assert.That(status.ServerInfo.Pid).IsGreaterThan(0);
        await Assert.That(status.ServerInfo.Version).IsNotEmpty();
    }

    [Test]
    public async Task Fixture_Should_Hand_Out_Isolated_Workspaces()
    {
        using var first = server.CreateWorkspace();
        using var second = server.CreateWorkspace();

        await Assert.That(first.Path).IsNotEqualTo(second.Path);
        await Assert.That(first.Path).Contains("workspaces");
    }
}
