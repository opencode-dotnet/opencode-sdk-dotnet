using System.Globalization;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Commands;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class CommandsClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ListCommandsAsync_Should_Report_The_Typed_Seeded_Command(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var response = await LiveReadiness.WaitAsync(
            token => client.Commands.ListCommandsAsync(cancellationToken: token),
            result => result.Commands.Any(item => item.Name == SimulationConfigSeed.CommandName),
            "seeded commands", cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Location.Directory).IsEqualTo(workspace.Path);
        var command = response.Commands.Single(item => item.Name == SimulationConfigSeed.CommandName);
        await Assert.That(command.Description).IsEqualTo(SimulationConfigSeed.CommandDescription);

        Console.WriteLine(
            "commands-live: status=" + Number(response.Status) + " name=" + command.Name);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
