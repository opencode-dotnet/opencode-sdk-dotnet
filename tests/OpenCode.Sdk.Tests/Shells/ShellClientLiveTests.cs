using System.Text;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Shells;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ShellClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(180_000)]
    public async Task GetOutputAsync_Should_Page_The_Captured_Bytes_When_A_Command_Has_Exited(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15));
        var probe = ConnectOutputProbe(client, cleanup, cancellationToken);
        Exception? primaryFailure = null;
        try
        {
            await probe.WaitForConnectedAsync(cancellationToken);
            var marker = "sdk-output-page-" + Guid.NewGuid().ToString("N");
            var created = await client.Shells.CreateShellAsync(new ShellCreateRequest { Command = "echo " + marker, Cwd = workspace.Path }, cancellationToken: cancellationToken);
            var shell = client.Shells.GetShellClient(created.Shell.Id);
            cleanup.Own("paged shell removal", async token => _ = await shell.RemoveAsync(cancellationToken: token));
            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            _ = await probe.WaitForAsync<ShellExited>(item => item.Data.Id == created.Shell.Id, "paged shell exit", barrier.Token);

            var first = await shell.GetOutputAsync(new ShellOutputRequest { Cursor = 0, Limit = 1 }, cancellationToken: cancellationToken);
            await Assert.That(first.Output.Output).IsEqualTo(marker[..1]);
            await Assert.That(first.Output.Cursor).IsEqualTo(1);
            await Assert.That(first.Output.Truncated).IsFalse();
            var rest = await shell.GetOutputAsync(new ShellOutputRequest { Cursor = first.Output.Cursor }, cancellationToken: cancellationToken);
            var combined = first.Output.Output + rest.Output.Output;
            await Assert.That(combined).IsEqualTo(marker + "\n").Or.IsEqualTo(marker + "\r\n");
            await Assert.That(rest.Output.Cursor).IsEqualTo(Encoding.UTF8.GetByteCount(combined));
            await Assert.That(rest.Output.Cursor).IsEqualTo(rest.Output.Size);
            await Assert.That(rest.Output.Truncated).IsFalse();
            var terminal = await shell.GetOutputAsync(new ShellOutputRequest { Cursor = rest.Output.Cursor }, cancellationToken: cancellationToken);
            await Assert.That(terminal.Output.Output).IsEmpty();
            await Assert.That(terminal.Output.Cursor).IsEqualTo(rest.Output.Size);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private static SessionEventProbe ConnectOutputProbe(OpenCodeClient client, OwnedCleanup cleanup, CancellationToken token)
    {
        var reader = new OwnedEventReader(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15), token);
        cleanup.Own("output event reader", TimeSpan.FromSeconds(75), _ => reader.CompleteAsync(null));
        var probe = new SessionEventProbe(reader);
        probe.Start(client.Events.SubscribeAsync(cancellationToken: reader.Token));
        return probe;
    }
}
