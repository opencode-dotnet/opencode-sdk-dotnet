using System.Text;
using System.Text.Json.Nodes;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.PersistentPtys;
using Testably.Abstractions;

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
        // Only contender ownership is injected: the actual located core runs through the ledger,
        // and production discovery and Stop retain their own clock roots and handoff behavior.
        doors.EnsureLocatedAsync(Arg.Any<EnsureRequest>(), Arg.Any<ServicePaths>(), Arg.Any<ServiceConfigEnvironment>(), Arg.Any<CancellationToken>())
            .Returns(call => context.EnsureLocatedAsync(call.Arg<EnsureRequest>(), call.Arg<ServicePaths>(),
                call.Arg<ServiceConfigEnvironment>(), call.Arg<CancellationToken>()));
        doors.DiscoverLocatedAsync(Arg.Any<ServicePaths>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => actual.DiscoverLocatedAsync(call.Arg<ServicePaths>(), call.ArgAt<string?>(1), call.Arg<CancellationToken>()));
        doors.StopLocatedAsync(Arg.Any<ServicePaths>(), Arg.Any<OpenCodeServerStopPtyMode?>(), Arg.Any<CancellationToken>())
            .Returns(call => actual.StopLocatedAsync(call.Arg<ServicePaths>(), call.Arg<OpenCodeServerStopPtyMode?>(), call.Arg<CancellationToken>()));
        await using var connection = await OpenCodeServer.ResolveWithSeamsAsync(new() { Service = service },
            new ConnectionSeams(doors, new ServiceEnvironment(), new ServiceFileSystem()), cancellationToken);
        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Service);
        await Assert.That(connection.Server).IsNotNull();
        await Assert.That(connection.ServerVersion).IsEqualTo("local");
        await Assert.That(connection.Server.RegisteredVersion).IsEqualTo("local");
        await Assert.That(connection.ServiceOperations).IsNotNull();
        // Keep an incorrect discovery miss safe and observable as a valid standalone result.
        doors.StartAsync(Arg.Any<OpenCodeServerOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new OpenCodeServer(connection.Endpoint, connection.Password, connection.Server.ProcessId, version: null));
        await using var discovered = await OpenCodeServer.ResolveWithSeamsAsync(new()
        {
            Autostart = false,
            Service = new OpenCodeServerEnsureOptions
            {
                RegistrationFilePath = context.RegistrationFile,
                ExpectedVersion = "not-local",
                VersionPolicy = OpenCodeServerVersionPolicy.Error,
            },
        }, new ConnectionSeams(doors, new ServiceEnvironment(), new ServiceFileSystem()), cancellationToken);
        await Assert.That(discovered.Mode).IsEqualTo(OpenCodeServerConnectionMode.Service);
        await Assert.That(discovered.ServerVersion).IsEqualTo("local");
        await Assert.That(discovered.Server).IsNotNull();
        await Assert.That(discovered.Server.ProcessId).IsEqualTo(connection.Server.ProcessId);
        _ = doors.DidNotReceiveWithAnyArgs().StartAsync(options: null, cancellationToken);
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

    [Test]
    [Timeout(180_000)]
    public async Task EnsureLocatedWithSeamsAsync_Should_Spawn_With_The_Captured_Config_Directory(CancellationToken cancellationToken)
    {
        await using var context = await EnsureServiceContext.CreateAsync(cancellationToken);
        var files = new RealFileSystem();
        var captured = files.Path.Combine(context.RunRoot, "captured-config");
        var changed = files.Path.Combine(context.RunRoot, "changed-config");
        foreach (var directory in new[] { captured, changed })
        {
            await SeedConfigAsync(files, files.Path.Combine(directory, "service-local.json"),
                new JsonObject { ["port"] = context.Port }, cancellationToken);
        }
        var configFile = files.Path.Combine(context.Environment["OPENCODE_CONFIG_DIR"], "service-local.json");
        await SeedConfigAsync(files, configFile, new JsonObject
        {
            ["env"] = new JsonObject { ["OPENCODE_CONFIG_DIR"] = changed },
        }, cancellationToken);
        var request = EnsureRequest.Snapshot(new OpenCodeServerEnsureOptions
        {
            RegistrationFilePath = context.RegistrationFile,
            Command = [context.ShimPath, "serve", "--service"],
            Environment = context.Environment.Where(entry => !string.Equals(entry.Key, "OPENCODE_CONFIG_DIR", StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
        });
        var paths = new ServicePaths(context.RegistrationFile, configFile, [], null);
        var overlay = new ServiceConfigEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_CONFIG_DIR"] = captured,
        });

        await using var server = await context.EnsureLocatedAsync(request, paths, overlay, cancellationToken);
        var directories = await context.ReadConfigDirectoriesAsync(server, cancellationToken);

        await Assert.That(directories).Contains(files.Path.GetFullPath(captured));
        await Assert.That(directories).DoesNotContain(files.Path.GetFullPath(changed));
        await Assert.That(server.RegisteredVersion).IsEqualTo("local");
    }

    private static async Task SeedConfigAsync(RealFileSystem files, string path, JsonObject config, CancellationToken cancellationToken)
    {
        _ = files.Directory.CreateDirectory(files.Path.GetDirectoryName(path)!);
        using var stream = files.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(config.ToJsonString()), cancellationToken);
    }
}
