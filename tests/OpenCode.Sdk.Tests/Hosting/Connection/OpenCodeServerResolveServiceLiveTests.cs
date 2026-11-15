using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.PersistentPtys;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

[NotInParallel]
[Category(TestCategories.ServiceElection)]
public sealed class OpenCodeServerResolveServiceLiveTests
{
    [Test]
    [Timeout(180_000)]
    public async Task ServiceOperations_Should_Reconnect_To_The_Same_Daemon_And_Restart_A_New_One(CancellationToken cancellationToken)
    {
        await using var context = await EnsureServiceContext.CreateAsync(cancellationToken);
        var service = new OpenCodeServerEnsureOptions
        {
            RegistrationFilePath = context.RegistrationFile,
            Command = [context.ShimPath, "serve", "--service"],
            Environment = context.Environment,
        };
        var doors = Substitute.For<IServerDoors>();
        var actual = new ServerDoors();
        // Keep every spawned contender in the context's existing ownership ledger. The direct
        // registration selection has no config overlay; the production Stop still does the handoff.
        doors.EnsureLocatedAsync(Arg.Any<EnsureRequest>(), Arg.Any<ServicePaths>(), Arg.Any<ServiceConfigEnvironment>(), Arg.Any<CancellationToken>())
            .Returns(call => context.EnsureAsync(new OpenCodeServerEnsureOptions
            {
                RegistrationFilePath = call.Arg<ServicePaths>().RegistrationFile,
                Command = call.Arg<EnsureRequest>().Command,
                Environment = call.Arg<EnsureRequest>().Environment,
                OnStart = call.Arg<EnsureRequest>().OnStart,
                VersionPolicy = call.Arg<EnsureRequest>().Policy,
                ExpectedVersion = call.Arg<EnsureRequest>().Selection.ExpectedVersion,
            }, call.Arg<CancellationToken>()));
        doors.StopLocatedAsync(Arg.Any<ServicePaths>(), Arg.Any<OpenCodeServerStopPtyMode?>(), Arg.Any<CancellationToken>())
            .Returns(call => actual.StopLocatedAsync(call.Arg<ServicePaths>(), call.Arg<OpenCodeServerStopPtyMode?>(), call.Arg<CancellationToken>()));
        await using var connection = await OpenCodeServer.ResolveWithSeamsAsync(new() { Service = service },
            new ConnectionSeams(doors, new ServiceEnvironment(), new ServiceFileSystem()), cancellationToken);
        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Service);
        await Assert.That(connection.Server).IsNotNull();
        await Assert.That(connection.ServerVersion).IsEqualTo("local");
        await Assert.That(connection.Server.RegisteredVersion).IsEqualTo("local");
        await Assert.That(connection.ServiceOperations).IsNotNull();
        await using var reconnected = await connection.ServiceOperations.ReconnectAsync(cancellationToken);
        await Assert.That(reconnected.ProcessId).IsEqualTo(connection.Server.ProcessId);
        using var client = connection.CreateClient(options => options.Location = new LocationSelector { Directory = context.RunRoot });
        var session = await client.Sessions.CreateSessionAsync(new SessionCreateRequest { Title = "resolver restart" }, cancellationToken: cancellationToken);
        var created = await client.PersistentPtys.CreatePersistentPtyAsync(session.Session.Id,
            PersistentPtyLiveTests.CreateRequest(), OpenCodeRequestOptions.NoThrow, cancellationToken);
        if (PersistentPtyDaemonGate.DaemonExpected)
        {
            await Assert.That(created.IsError).IsFalse();
            Console.WriteLine($"resolver-service-live: daemon-present; terminal={created.PersistentPty.Id}");
        }
        else
        {
            await Assert.That(created.Status).IsEqualTo(503);
            Console.WriteLine($"resolver-service-live: daemon-absent; status={created.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            await Assert.That(created.Error).IsTypeOf<ServiceUnavailableError>();
            await Assert.That(((ServiceUnavailableError)created.Error!).Service).IsEqualTo("opencode-pty");
        }

        await using var restarted = await connection.ServiceOperations.RestartAsync(cancellationToken);

        await Assert.That(restarted.ProcessId).IsNotEqualTo(reconnected.ProcessId);
        await Assert.That(restarted.RegisteredVersion).IsEqualTo("local");
        await Assert.That(connection.ServerVersion).IsEqualTo("local");
        using var after = restarted.CreateClient(options => options.Location = new LocationSelector { Directory = context.RunRoot });
        var info = await after.Server.GetInfoAsync(cancellationToken: cancellationToken);
        await Assert.That(info.ServerInfo.Pid).IsEqualTo(restarted.ProcessId);
        if (PersistentPtyDaemonGate.DaemonExpected)
        {
            var listed = await after.PersistentPtys.ListPersistentPtysAsync(session.Session.Id, cancellationToken: cancellationToken);
            await Assert.That(listed.PersistentPtys.Select(terminal => terminal.Id)).Contains(created.PersistentPty.Id);
        }
    }
}
