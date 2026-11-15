using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServerResolveLiveTests(PinnedOpenCodeServerFixture fixture)
{
    [Test]
    [Timeout(120_000)]
    public async Task ResolveAsync_Should_Reach_The_Pinned_Server_Explicitly(CancellationToken cancellationToken)
    {
        using var plain = fixture.CreateClient();
        var independent = await plain.Server.GetInfoAsync(cancellationToken: cancellationToken);
        var external = ExternalServerEndpoint.FromEnvironment();
        var password = fixture.IsExternal ? external?.Password : fixture.Server.Password;
        var sourceRun = !fixture.IsExternal && PinnedServerCommandOverride.FromEnvironment() is null;
        var expected = sourceRun ? "local" : independent.ServerInfo.Version;
        var arm = sourceRun ? "source-run" : "installed-command";
        if (fixture.IsExternal)
        {
            arm = "external";
        }
        Console.WriteLine($"resolver-live: arm={arm}; version={expected}");
        var contexts = new List<OpenCodeServerVersionMismatchContext>();
        await using var connection = await OpenCodeServer.ResolveAsync(new()
        {
            Endpoint = fixture.Endpoint,
            Password = password,
            OnServerVersionMismatch = contexts.Add,
        }, cancellationToken);

        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Explicit);
        await Assert.That(connection.ServerVersion).IsEqualTo(expected);
        await Assert.That(contexts.Count).IsEqualTo(string.Equals(expected, "2.3.0", StringComparison.Ordinal) ? 0 : 1);
        if (contexts.Count != 0)
        {
            await Assert.That(contexts[0].Endpoint).IsEqualTo(fixture.Endpoint);
            await Assert.That(contexts[0].ServerVersion).IsEqualTo(expected);
            await Assert.That(contexts[0].ClientVersion).IsEqualTo("2.3.0");
        }
        using var client = connection.CreateClient();
        var reached = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);
        await Assert.That(reached.ServerInfo.Pid).IsEqualTo(independent.ServerInfo.Pid);
        await Assert.That(reached.ServerInfo.Version).IsEqualTo(expected);
        var failure = await Assert.That(async () => _ = await OpenCodeServer.ResolveAsync(new()
        {
            Endpoint = fixture.Endpoint,
            Password = "incorrect-" + Guid.NewGuid().ToString("N"),
        }, cancellationToken)).Throws<OpenCodeServerException>();
        await Assert.That(failure!.Message).IsEqualTo($"Server at {fixture.Endpoint} rejected the password");
    }

    [Test]
    [Timeout(120_000)]
    public async Task ResolveAsync_Should_Start_And_End_A_Standalone_Server(CancellationToken cancellationToken)
    {
        var files = new RealFileSystem();
        using var root = new TestRunRoot(files);
        var isolation = ServerIsolation.For(files, root.Path);
        var command = new PinnedServerCommand(files);
        await using var connection = await OpenCodeServer.ResolveAsync(new()
        {
            Standalone = true,
            Launch = new OpenCodeServerOptions
            {
                Command = command.Resolve(),
                WorkingDirectory = command.WorkingDirectory,
                Environment = isolation.Environment,
            },
        }, cancellationToken);
        isolation.ConfirmHonored("The resolver's standalone server");
        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Standalone);
        await Assert.That(connection.Server).IsNotNull();
        await Assert.That(connection.Server.OwnsProcess).IsTrue();
        await Assert.That(connection.ServerVersion).IsNull();
        using var client = connection.CreateClient();
        var info = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);
        await Assert.That(info.ServerInfo.Pid).IsEqualTo(connection.Server.ProcessId);

        await connection.DisposeAsync();

        var exit = await connection.Server.Exited.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        await Assert.That(exit.Kind).IsNotEqualTo(OpenCodeServerExitKind.Unknown);
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(
            connection.Server.ProcessId, TimeSpan.FromSeconds(30), cancellationToken)).IsTrue();
    }
}
