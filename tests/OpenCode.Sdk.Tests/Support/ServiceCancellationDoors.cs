using System.IO.Abstractions;
using System.Net;
using System.Text;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class ServiceCancellationDoors(
    IFileSystem fileSystem,
    Func<Func<Task>, Task<OperationCanceledException>> observeCancellation)
{
    private const string HandoffPath = "/api/experimental/persistent-pty/handoff";
    private const string ShutdownPath = "/api/experimental/persistent-pty/shutdown";
    private const string ServicePassword = "synthetic-identity-password";

    internal async Task<OperationCanceledException> AtResolveConfigStoreReadAsync(CancellationTokenSource caller)
    {
        var scenario = new ServiceConfigScenario();
        var forwarded = default(CancellationToken);
        scenario.FileSystem.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                forwarded = call.Arg<CancellationToken>();
                await caller.CancelOnWorkerAsync();
                forwarded.ThrowIfCancellationRequested();
                return null;
            });
        var seams = new ConnectionSeams(
            Substitute.For<IServerDoors>(), scenario.Environment, scenario.FileSystem, ProbeTime: scenario.Time);
        var failure = await observeCancellation(async () =>
        {
            await using var connection = await OpenCodeServer.ResolveWithSeamsAsync(
                new() { Service = new OpenCodeServerEnsureOptions { Channel = "local" } }, seams, caller.Token);
        });

        await Assert.That(forwarded).IsEqualTo(caller.Token);
        return failure;
    }

    internal async Task<OperationCanceledException> AtConfigStopPreCancelledAsync(CancellationTokenSource caller)
    {
        var inventory = fileSystem.Directory.GetFiles(SessionIsolation.Root!, "*", SearchOption.AllDirectories);
        await caller.CancelOnWorkerAsync();
        var failure = await observeCancellation(() => OpenCodeServiceConfiguration.SetHostnameAsync("localhost", cancellationToken: caller.Token));
        await Assert.That(fileSystem.Directory.GetFiles(SessionIsolation.Root!, "*", SearchOption.AllDirectories))
            .IsEquivalentTo(inventory, CollectionOrdering.Any);
        return failure;
    }

    internal async Task<OperationCanceledException> AtConfigPreCancelledAsync(string row, CancellationTokenSource caller)
    {
        var options = new OpenCodeServiceConfigOptions();
        var inventory = fileSystem.Directory.GetFiles(SessionIsolation.Root!, "*", SearchOption.AllDirectories);
        await caller.CancelOnWorkerAsync();
        var failure = await observeCancellation(() => row == "service-config-read"
            ? OpenCodeServiceConfiguration.ReadAsync(options, caller.Token)
            : OpenCodeServiceConfiguration.GetOrCreatePasswordAsync(options, caller.Token));

        await Assert.That(fileSystem.Directory.GetFiles(SessionIsolation.Root!, "*", SearchOption.AllDirectories))
            .IsEquivalentTo(inventory, CollectionOrdering.Any);
        return failure;
    }

    internal async Task<OperationCanceledException> AtStopPreCancelledAsync(CancellationTokenSource caller)
    {
        using var root = new TestRunRoot(fileSystem);
        await using var daemon = LoopbackHttpServer.Start(_ => new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.OK,
            ContentType = "application/json",
            Body = ServiceInfoBodyData.Ready,
        });
        var registrationPath = fileSystem.Path.Combine(root.Path, "service.json");
        var registrationFiles = new TestablyServiceFileSystem(fileSystem);
        var document = await WriteServiceRegistrationAsync(registrationFiles, registrationPath, daemon.Endpoint);

        await caller.CancelOnWorkerAsync();
        var failure = await observeCancellation(() => InvokeServiceAsync("stop", registrationPath, daemon.Endpoint, caller.Token));

        await Assert.That(daemon.RequestPaths).IsEmpty();
        var retained = await registrationFiles.TryReadAllBytesAsync(registrationPath, CancellationToken.None);
        await Assert.That(retained).IsEquivalentTo(Encoding.UTF8.GetBytes(document), CollectionOrdering.Matching);
        return failure;
    }

    internal async Task<OperationCanceledException> AtServiceRequestAsync(string row, string stage, CancellationTokenSource caller)
    {
        using var root = new TestRunRoot(fileSystem);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldPath = (row, stage) switch
        {
            ("discover" or "incumbent", _) => "/api/info",
            ("resolve" or "reconnect", _) => "/api/info",
            (_, "ticket") => HandoffPath,
            _ => ShutdownPath,
        };
        await using var daemon = LoopbackHttpServer.Start(path =>
        {
            if (path == heldPath)
            {
                _ = entered.TrySetResult(true);
                return new LoopbackHttpResponse
                {
                    StatusCode = HttpStatusCode.OK,
                    ContentType = "application/json",
                    Body = ServiceInfoBodyData.Ready,
                    KeepOpen = true,
                };
            }

            return new LoopbackHttpResponse
            {
                StatusCode = path == HandoffPath ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                ContentType = "application/json",
                Body = ServiceInfoBodyData.Ready,
            };
        });
        var registrationPath = fileSystem.Path.Combine(root.Path, "service.json");
        var registrationFiles = new TestablyServiceFileSystem(fileSystem);
        var document = await WriteServiceRegistrationAsync(registrationFiles, registrationPath, daemon.Endpoint);
        var observed = observeCancellation(() => InvokeServiceAsync(row, registrationPath, daemon.Endpoint, caller.Token));
        try
        {
            Task[] signals = [entered.Task];
            await Task.WhenAll(signals).WaitAsync(HangGuard.Bound, CancellationToken.None);
            await caller.CancelOnWorkerAsync();
            var failure = await observed;
            var request = daemon.Requests.Single(request => request.Path == heldPath);
            await Assert.That(request.Headers["Authorization"]).IsEqualTo(
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + ServicePassword)));
            if (heldPath == ShutdownPath)
            {
                await Assert.That(request.Headers).DoesNotContainKey("x-opencode-directory");
            }

            string[] expectedPaths = (row, stage) switch
            {
                ("discover" or "incumbent", _) => ["/api/info"],
                ("resolve" or "reconnect", _) => ["/api/info"],
                ("restart", _) => [heldPath],
                (_, "fallback") => ["/api/info", HandoffPath, ShutdownPath],
                _ => ["/api/info", heldPath],
            };
            await Assert.That(daemon.RequestPaths).IsEquivalentTo(expectedPaths, CollectionOrdering.Matching);
            var retained = await registrationFiles.TryReadAllBytesAsync(registrationPath, CancellationToken.None);
            await Assert.That(retained).IsEquivalentTo(Encoding.UTF8.GetBytes(document), CollectionOrdering.Matching);
            await Assert.That(fileSystem.Directory.GetFiles(root.Path, "*" + ServicePtyHandoff.SidecarSuffix)).IsEmpty();
            return failure;
        }
        finally
        {
            // The response stays held until cancellation settled: Ensure cannot reach termination
            // or recruit a contender.
            await caller.CancelOnWorkerAsync();
            try
            {
                _ = await observed;
            }
            finally
            {
                daemon.ReleaseResponses();
            }
        }
    }

    private static async Task<string> WriteServiceRegistrationAsync(
        TestablyServiceFileSystem registrationFiles, string registrationPath, Uri endpoint)
    {
        var document = ServiceRegistrationDocument.Compose("srv_identity", ServiceInfoBodyData.Version,
            endpoint, ServiceInfoBodyData.Pid, ServicePassword);
        await Assert.That(await registrationFiles.TryCreateExclusiveAsync(
            registrationPath, Encoding.UTF8.GetBytes(document), CancellationToken.None)).IsTrue();
        return document;
    }

    private static async Task InvokeServiceAsync(string row, string registrationPath, Uri endpoint, CancellationToken caller)
    {
        if (row == "stop")
        {
            await OpenCodeServer.StopAsync(new OpenCodeServerStopOptions { RegistrationFilePath = registrationPath }, caller);
        }
        else if (row == "discover")
        {
            await using var server = await OpenCodeServer.DiscoverAsync(
                new OpenCodeServerDiscoverOptions { RegistrationFilePath = registrationPath }, caller);
        }
        else if (row == "incumbent")
        {
            _ = await OpenCodeServer.FindIncumbentAsync(endpoint,
                new OpenCodeServerDiscoverOptions { RegistrationFilePath = registrationPath }, caller);
        }
        else if (row == "shutdown-persistent-pty")
        {
            await OpenCodeServer.ShutdownPersistentPtyAsync(
                new OpenCodeServerDiscoverOptions { RegistrationFilePath = registrationPath }, caller);
        }
        else if (row is "resolve" or "reconnect" or "restart")
        {
            await InvokeConnectionAsync(row, registrationPath, endpoint, caller);
        }
        else
        {
            await using var server = await OpenCodeServer.EnsureAsync(new OpenCodeServerEnsureOptions
            {
                RegistrationFilePath = registrationPath,
                ExpectedVersion = "0.0.0-other",
                VersionPolicy = OpenCodeServerVersionPolicy.Replace,
                Command = ["bun", "--version"],
            }, caller);
        }
    }

    private static async Task InvokeConnectionAsync(string row, string registrationPath, Uri endpoint, CancellationToken caller)
    {
        if (row == "resolve")
        {
            await using var connection = await OpenCodeServer.ResolveAsync(new OpenCodeServerResolveOptions
            {
                Endpoint = endpoint,
                Password = ServicePassword,
            }, caller);
            return;
        }

        var snapshot = new ManagedServiceSnapshot(
            EnsureRequest.Snapshot(new OpenCodeServerEnsureOptions { RegistrationFilePath = registrationPath }),
            new ServicePaths(registrationPath, ConfigFile: null, LegacyRegistrationFiles: [], LegacyConfigFile: null),
            new ServiceConfigEnvironment(Entries: null));
        var operations = new OpenCodeServerServiceOperations(snapshot, new ServerDoors());
        await using var server = row == "reconnect"
            ? await operations.ReconnectAsync(caller)
            : await operations.RestartAsync(caller);
    }
}
