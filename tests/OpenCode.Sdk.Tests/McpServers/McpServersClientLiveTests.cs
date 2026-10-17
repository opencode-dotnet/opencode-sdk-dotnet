using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.McpServers;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class McpServersClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(180_000)]
    public async Task ListMcpServersAsync_Should_Report_The_Disabled_Entry_When_It_Was_Added_At_Runtime(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var name = "sdk-catalog-" + Guid.NewGuid().ToString("N");
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15));
        cleanup.Own("owned catalog MCP removal", async token => await DisabledMcpServer.RemoveOwnedAsync(client, name, token));
        var reader = new OwnedEventReader(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15), cancellationToken);
        cleanup.Own("catalog event reader", TimeSpan.FromSeconds(75), _ => reader.CompleteAsync(null));
        var probe = new SessionEventProbe(reader);
        Exception? failure = null;
        try
        {
            probe.Start(client.Events.SubscribeAsync(cancellationToken: reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);
            await DisabledMcpServer.AddAsync(client, name, cancellationToken);

            // A finite read kills an empty-list mutant before any event wait can stall.
            var response = await client.McpServers.ListMcpServersAsync(cancellationToken: cancellationToken);
            var owned = response.McpServers.Where(entry => entry.Name == name).ToList();
            await Assert.That(owned.Select(entry => entry.Name)).Contains(name);
            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var changed = await probe.WaitForAsync<McpStatusChanged>(entry => entry.Data.Server == name,
                "owned catalog MCP registration", barrier.Token);
            await Assert.That(changed.Data.Server).IsEqualTo(name);

            var reconciled = await client.McpServers.ListMcpServersAsync(cancellationToken: cancellationToken);
            await Assert.That(reconciled.McpServers.Single(entry => entry.Name == name).Status).IsTypeOf<McpStatusDisabled>();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await cleanup.CompleteAsync(failure);
    }
}
