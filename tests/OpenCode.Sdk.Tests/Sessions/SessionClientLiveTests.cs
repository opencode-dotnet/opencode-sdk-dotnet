using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string ModelId = "sim-model";
    private const string ProviderId = "sim";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);

    [Test]
    [Timeout(180_000)]
    public async Task RunCommandAsync_Should_Expand_The_Seeded_Command_When_Arguments_Are_Supplied(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-command-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
            Model = new ModelRef { Id = ModelId, ProviderId = ProviderId },
        }, cancellationToken: cancellationToken);
        var sessionId = created.Session.Id;
        var session = client.Sessions.GetSessionClient(sessionId);
        var sessionCleanup = new OwnedSessionInboxCleanup(session, client.Experimental, sessionId, server.Controller, CleanupTimeout);
        var reader = new OwnedEventReader(EventWait, CleanupTimeout, cancellationToken);
        var probe = new SessionEventProbe(reader);
        const string reply = "Seeded command coverage reply.";
        Exception? primaryFailure = null;

        try
        {
            probe.Start(client.Events.SubscribeAsync(reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);
            _ = await session.RunCommandAsync(new SessionCommandRequest
            {
                Name = SimulationConfigSeed.CommandName,
                Text = "coverage-argument",
            }, cancellationToken: cancellationToken);
            var invocation = sessionCleanup.RetainInvocation(
                await server.Controller.WaitForRequestAsync(TimeSpan.FromSeconds(60)));
            await invocation.ChunkTextAsync(reply);
            await invocation.FinishAsync();
            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var ended = await probe.WaitForAsync<SessionTextEnded>(
                item => item.Data.SessionId == sessionId && item.Data.Text == reply,
                "command reply text end", barrier.Token);
            _ = await probe.WaitForAsync<SessionExecutionSucceeded>(
                item => item.Data.SessionId == sessionId,
                "command execution success", ended, barrier.Token);
            var wait = client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: cancellationToken);
            sessionCleanup.RetainWait(wait);
            _ = await wait;

            var messages = await session.ListMessagesAsync(
                new SessionMessageListRequest { Order = ListOrder.Ascending }, cancellationToken: cancellationToken);
            await Assert.That(messages.Messages.OfType<SessionMessageUser>().Select(message => message.Text))
                .Contains("Report the live catalog for coverage-argument");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            await reader.CompleteAsync(primaryFailure);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await sessionCleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task RunShellCommandAsync_Should_Record_The_Command_Output_When_The_Command_Exits(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-shell-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
        }, cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(created.Session.Id);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        cleanup.MarkTurnCompleted();
        var messageId = "msg_shell_" + Guid.NewGuid().ToString("N");
        var marker = "sdk-shell-" + Guid.NewGuid().ToString("N");
        var command = "echo " + marker;
        Exception? primaryFailure = null;

        try
        {
            _ = await session.RunShellCommandAsync(new SessionShellRequest
            {
                Id = messageId,
                Command = command,
            }, cancellationToken: cancellationToken);
            var messages = await session.ListMessagesAsync(cancellationToken: cancellationToken);
            var shells = messages.Messages.OfType<SessionMessageShell>().Where(message => message.Id == messageId).ToList();
            foreach (var shell in shells)
            {
                cleanup.Own("shell removal", async token =>
                    _ = await client.Shells.GetShellClient(shell.ShellId).RemoveAsync(cancellationToken: token));
            }

            await Assert.That(shells).Count().IsEqualTo(1);
            await Assert.That(shells[0].Command).IsEqualTo(command);
            await Assert.That(shells[0].Status).IsEqualTo(SessionMessageShellStatus.Exited);
            await Assert.That(shells[0].Exit).IsEqualTo(0);
            await Assert.That(shells[0].Output).IsNotNull();
            await Assert.That(shells[0].Output!.Output).Contains(marker);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task SwitchAgentAsync_Should_Store_The_Selected_Agent_When_It_Differs_From_The_Current_Agent(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-agent-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
            Agent = SimulationConfigSeed.PermissionProbeAgentId,
        }, cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(created.Session.Id);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        cleanup.MarkTurnCompleted();
        Exception? primaryFailure = null;

        try
        {
            var before = await session.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(before.Session.Agent).IsEqualTo(SimulationConfigSeed.PermissionProbeAgentId);
            // Unlike agent.list, integration.list joins this location's plugin activation.
            _ = await client.Integrations.ListIntegrationsAsync(cancellationToken: cancellationToken);
            var agents = await client.Agents.ListAgentsAsync(cancellationToken: cancellationToken);
            await Assert.That(agents.Agents.Select(agent => agent.Id)).Contains(SimulationConfigSeed.ReadAskAgentId);

            _ = await session.SwitchAgentAsync(new SessionSwitchAgentRequest
            {
                Agent = SimulationConfigSeed.ReadAskAgentId,
            }, cancellationToken: cancellationToken);

            var after = await session.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(after.Session.Agent).IsEqualTo(SimulationConfigSeed.ReadAskAgentId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task SwitchModelAsync_Should_Store_The_Explicit_Model_When_The_Session_Has_No_Override(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-model-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
        }, cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(created.Session.Id);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        cleanup.MarkTurnCompleted();
        Exception? primaryFailure = null;

        try
        {
            var before = await session.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(before.Session.Model).IsNull();

            _ = await session.SwitchModelAsync(new SessionSwitchModelRequest
            {
                Model = new ModelRef { Id = SimulationConfigSeed.ModelId, ProviderId = SimulationConfigSeed.ProviderId },
            }, cancellationToken: cancellationToken);

            var after = await session.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(after.Session.Model).IsNotNull();
            await Assert.That(after.Session.Model!.Id).IsEqualTo(SimulationConfigSeed.ModelId);
            await Assert.That(after.Session.Model.ProviderId).IsEqualTo(SimulationConfigSeed.ProviderId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task MarkViewedAsync_Should_Record_The_Idle_Watermark_When_A_Completed_Session_Is_Viewed(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-view-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
            Model = new ModelRef { Id = ModelId, ProviderId = ProviderId },
        }, cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(created.Session.Id);
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        Exception? primaryFailure = null;

        try
        {
            var turn = new SimulatedSessionTurn(server, client, session, created.Session.Id);
            _ = await turn.CompleteAsync("Viewed watermark coverage prompt", "Viewed watermark coverage reply.", cancellationToken);
            _ = await client.Experimental.WaitForSessionAsync(created.Session.Id, cancellationToken: cancellationToken);
            cleanup.MarkTurnCompleted();
            var before = await session.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(before.Session.Time.Idle).IsNotNull();
            await Assert.That(before.Session.Time.Viewed).IsNull();

            _ = await session.MarkViewedAsync(new SessionViewRequest
            {
                Idle = before.Session.Time.Idle!.Value,
            }, cancellationToken: cancellationToken);

            var after = await session.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(after.Session.Time.Viewed).IsEqualTo(before.Session.Time.Idle);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task SetEnvironmentAsync_Should_Use_The_Replacement_Environment_When_A_Session_Shell_Starts(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        using var configClient = server.CreateClient();
        var cleanup = new OwnedCleanup(CleanupTimeout);
        OwnedSessionCleanup? sessionCleanup = null;
        var messageId = "msg_environment_" + Guid.NewGuid().ToString("N");
        var variable = "SDK_SESSION_ENV_" + Guid.NewGuid().ToString("N");
        var marker = "sdk-environment-" + Guid.NewGuid().ToString("N");
        Exception? primaryFailure = null;

        try
        {
            var previousShell = (await configClient.Config.GetConfigAsync(cancellationToken: cancellationToken)).Config.OfType<ConfigDocument>()
                .Select(document => document.Info.Shell).LastOrDefault(shell => shell is not null);
            cleanup.Own("restore isolated shell config", async token =>
                _ = await configClient.Experimental.UpdateConfigAsync(
                    new ExperimentalConfigUpdateRequest { Shell = previousShell }, cancellationToken: token));
            var catalog = await configClient.Config.GetShellsAsync(cancellationToken: cancellationToken);
            var selected = catalog.Shells.First(shell => shell.Acceptable);
            // Persist before this test's location is first resolved: config updates do not join reloads.
            _ = await configClient.Experimental.UpdateConfigAsync(
                new ExperimentalConfigUpdateRequest { Shell = selected.Path }, cancellationToken: cancellationToken);
            var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
            {
                Title = "session-environment-live",
                Location = new LocationPublicRef { Directory = workspace.Path },
            }, cancellationToken: cancellationToken);
            var session = client.Sessions.GetSessionClient(created.Session.Id);
            sessionCleanup = new OwnedSessionCleanup(session, CleanupTimeout);
            sessionCleanup.MarkTurnCompleted();
            var shellName = new Testably.Abstractions.RealFileSystem().Path.GetFileNameWithoutExtension(selected.Path).ToLowerInvariant();
            var command = shellName switch
            {
                "cmd" => "echo %" + variable + "%",
                "powershell" or "pwsh" => "echo $env:" + variable,
                _ => "echo \"$" + variable + "\"",
            };

            _ = await session.SetEnvironmentAsync(new SessionEnvironmentRequest
            {
                Variables = new Dictionary<string, string>(StringComparer.Ordinal) { [variable] = marker },
            }, cancellationToken: cancellationToken);
            _ = await session.RunShellCommandAsync(new SessionShellRequest
            {
                Id = messageId,
                Command = command,
            }, cancellationToken: cancellationToken);
            var messages = await session.ListMessagesAsync(cancellationToken: cancellationToken);
            var shells = messages.Messages.OfType<SessionMessageShell>().Where(message => message.Id == messageId).ToList();
            foreach (var shell in shells)
            {
                sessionCleanup.Own("environment shell removal", async token =>
                    _ = await client.Shells.GetShellClient(shell.ShellId).RemoveAsync(cancellationToken: token));
            }

            await Assert.That(shells).Count().IsEqualTo(1);
            await Assert.That(shells[0].Status).IsEqualTo(SessionMessageShellStatus.Exited);
            await Assert.That(shells[0].Exit).IsEqualTo(0);
            await Assert.That(shells[0].Output).IsNotNull();
            await Assert.That(shells[0].Output!.Output.Trim()).IsEqualTo(marker);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (sessionCleanup is not null)
        {
            try
            {
                await sessionCleanup.CompleteAsync(primaryFailure);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task PostMoveAsync_Should_Apply_The_Move_Before_The_Destination_Read(
        CancellationToken cancellationToken)
    {
        using var source = server.CreateWorkspace();
        using var destination = server.CreateWorkspace();
        using var sourceClient = server.CreateClient(new LocationSelector { Directory = source.Path });
        using var destinationClient = server.CreateClient(new LocationSelector { Directory = destination.Path });
        var created = await sourceClient.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-move-live",
            Location = new LocationPublicRef { Directory = source.Path },
            Model = new ModelRef { Id = ModelId, ProviderId = ProviderId },
        }, cancellationToken: cancellationToken);
        var sessionId = created.Session.Id;
        var sourceSession = sourceClient.Sessions.GetSessionClient(sessionId);
        var cleanup = CreateCleanup(sourceSession, sessionId);
        var reader = new OwnedEventReader(EventWait, CleanupTimeout, cancellationToken);
        var probe = new SessionEventProbe(reader);
        Exception? primaryFailure = null;

        try
        {
            // The subscription is attached before the move is admitted, so the applied event is
            // observed by construction rather than by racing the subscription.
            probe.Start(sourceClient.Events.SubscribeAsync(reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);

            var admitted = await sourceSession.MoveAsync(
                new SessionMoveRequest { Directory = destination.Path },
                cancellationToken: cancellationToken);
            await Assert.That(admitted.Status).IsEqualTo(204);
            await Assert.That(admitted.IsError).IsFalse();

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var moved = await probe.WaitForAsync<SessionMoved>(
                applied => applied.Data.SessionId == sessionId
                    && applied.Data.Location.Directory == destination.Path,
                "session.moved carrying the owned session at the destination", barrier.Token);
            await Assert.That(moved.Data.ProjectId).IsNotEmpty();

            var destinationSession = destinationClient.Sessions.GetSessionClient(sessionId);
            var read = await destinationSession.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(read.Status).IsEqualTo(200);
            await Assert.That(read.Session.Id).IsEqualTo(sessionId);
            await Assert.That(read.Session.Location.Directory).IsEqualTo(destination.Path);
            await Assert.That(read.Session.ProjectId).IsEqualTo(moved.Data.ProjectId);

            Console.WriteLine(
                "session-move-live: admission=" + Number(admitted.Status) +
                " applied-event=session.moved destination-project=" + moved.Data.ProjectId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (!exception.Data.Contains(EventDiagnosticSummary.DataKey))
            {
                exception.Data[EventDiagnosticSummary.DataKey] = probe.DiagnosticSummary;
            }
        }
        finally
        {
            await CompleteCleanupAsync(reader, cleanup, primaryFailure);
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task PostMoveAsync_Should_Return_The_Typed_404_For_A_Missing_Session(
        CancellationToken cancellationToken)
    {
        using var destination = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = destination.Path });
        var sessionId = "ses_sdk_missing_" + Guid.NewGuid().ToString("N");
        var response = await client.Sessions.GetSessionClient(sessionId).MoveAsync(
            new SessionMoveRequest { Directory = destination.Path },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.Error).IsTypeOf<SessionNotFoundError>();
        var error = response.Error as SessionNotFoundError;
        await Assert.That(error?.Tag).IsEqualTo("SessionNotFoundError");
        await Assert.That(error?.SessionId).IsEqualTo(sessionId);

        Console.WriteLine(
            "session-move-missing-live: status=" + Number(response.Status) + " error=" + error?.Tag);
    }

    private static OwnedSessionCleanup CreateCleanup(SessionClient session, string sessionId)
    {
        var cleanup = new OwnedSessionCleanup(
            _ => Task.CompletedTask,
            token => RemoveOwnedSessionAsync(session, sessionId, token),
            CleanupTimeout);
        cleanup.MarkTurnCompleted();
        return cleanup;
    }

    private static async Task RemoveOwnedSessionAsync(
        SessionClient session,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var response = await session.RemoveAsync(OpenCodeRequestOptions.NoThrow, cancellationToken);
        if (response.Status == 204
            || (response is { Status: 404, Error: SessionNotFoundError missing }
                && missing.SessionId == sessionId))
        {
            return;
        }

        throw new InvalidOperationException(
            "Owned session cleanup returned status " + Number(response.Status) +
            ", error " + response.Error?.GetType().Name + ", body " + response.RawBody + ".");
    }

    private static async Task CompleteCleanupAsync(
        OwnedEventReader reader,
        OwnedSessionCleanup cleanup,
        Exception? primaryFailure)
    {
        try
        {
            await reader.CompleteAsync(primaryFailure);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
