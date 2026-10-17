using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Shells;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ShellsClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(180_000)]
    public async Task CreateShellAsync_Should_Start_The_Command_When_The_Workspace_Is_Owned(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15));
        Exception? primaryFailure = null;
        try
        {
            var probe = await StartCreationEventsAsync(client, cleanup, cancellationToken);
            var marker = "sdk-create-shell-" + Guid.NewGuid().ToString("N");
            var command = "echo " + marker;

            var created = await client.Shells.CreateShellAsync(new ShellCreateRequest { Command = command, Cwd = workspace.Path }, cancellationToken: cancellationToken);
            var shell = client.Shells.GetShellClient(created.Shell.Id);
            cleanup.Own("created shell removal", async token => _ = await shell.RemoveAsync(cancellationToken: token));
            // A fabricated admission must fail on finite state, not a missing exit event.
            var stored = await shell.GetAsync(requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: cancellationToken);
            await Assert.That(stored.Status).IsEqualTo(200);
            await Assert.That(stored.Shell.Command).IsEqualTo(command);
            await Assert.That(stored.Shell.Cwd.Replace('\\', '/')).IsEqualTo(workspace.Path.Replace('\\', '/'));
            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            _ = await probe.WaitForAsync<ShellExited>(item => item.Data.Id == created.Shell.Id, "created shell exit", barrier.Token);
            var exited = await shell.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(exited.Shell.Status).IsEqualTo(ShellInfoStatus.Exited);
            await Assert.That(exited.Shell.Exit).IsEqualTo(0);
            var output = await shell.GetOutputAsync(cancellationToken: cancellationToken);
            await Assert.That(output.Output.Output.Trim()).IsEqualTo(marker);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private static async Task<SessionEventProbe> StartCreationEventsAsync(OpenCodeClient client, OwnedCleanup cleanup, CancellationToken token)
    {
        var stepTimeout = TimeSpan.FromSeconds(15);
        var reader = new OwnedEventReader(TimeSpan.FromSeconds(120), stepTimeout, token);
        cleanup.Own("shell creation events", TimeSpan.FromTicks(stepTimeout.Ticks * 5), _ => reader.CompleteAsync(null));
        var probe = new SessionEventProbe(reader);
        probe.Start(client.Events.SubscribeAsync(cancellationToken: reader.Token));
        await probe.WaitForConnectedAsync(token);
        return probe;
    }
}
