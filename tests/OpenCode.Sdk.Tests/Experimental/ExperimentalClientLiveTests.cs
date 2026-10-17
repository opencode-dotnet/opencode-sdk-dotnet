using System.Text.Json.Nodes;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Sessions.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Experimental;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ExperimentalClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string InstructionKey = "sdk-coverage-entry";
    private const string InitialValue = "Initial owned instruction";
    private const string ReplacementValue = "Replacement owned instruction";

    [Test]
    [Timeout(180_000)]
    public async Task AddMcpServerAsync_Should_Register_The_Disabled_Entry_When_No_Connection_Is_Requested(CancellationToken cancellationToken)
    {
        await WithMcpEntryAsync(async (client, name, probe) =>
        {
            var before = await client.McpServers.ListMcpServersAsync(cancellationToken: cancellationToken);
            await Assert.That(before.McpServers.Select(item => item.Name)).DoesNotContain(name);

            await AddDisabledMcpAsync(client, name, cancellationToken);

            // Check finite state before waiting for the event a no-op could never publish.
            var after = await client.McpServers.ListMcpServersAsync(cancellationToken: cancellationToken);
            await Assert.That(after.McpServers.Select(item => item.Name)).Contains(name);
            _ = await WaitForMcpChangeAsync(probe, name, cancellationToken);
            await AssertDisabledMcpAsync(client, name, cancellationToken);
        }, cancellationToken);
    }

    [Test]
    [Timeout(180_000)]
    public async Task RemoveMcpServerAsync_Should_Remove_The_Entry_When_It_Exists(CancellationToken cancellationToken)
    {
        await WithMcpEntryAsync(async (client, name, probe) =>
        {
            await AddDisabledMcpAsync(client, name, cancellationToken);
            var added = await WaitForMcpChangeAsync(probe, name, cancellationToken);
            await AssertDisabledMcpAsync(client, name, cancellationToken);

            _ = await client.Experimental.RemoveMcpServerAsync(name, cancellationToken: cancellationToken);

            // Check finite state before waiting for the event a no-op could never publish.
            var after = await client.McpServers.ListMcpServersAsync(cancellationToken: cancellationToken);
            await Assert.That(after.McpServers.Select(item => item.Name)).DoesNotContain(name);
            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            _ = await probe.WaitForAsync<McpStatusChanged>(item => item.Data.Server == name, "owned MCP removal", added, barrier.Token);
        }, cancellationToken);
    }

    [Test]
    [Timeout(60_000)]
    public async Task ListSessionInstructionsEntriesAsync_Should_Return_The_Entry_When_It_Was_Set(CancellationToken cancellationToken)
    {
        await WithInstructionEntryAsync(async (client, sessionId) =>
        {
            var response = await client.Experimental.ListSessionInstructionsEntriesAsync(sessionId, cancellationToken: cancellationToken);

            await Assert.That(response.SessionInstructionsEntries.Select(entry => entry.Key)).Contains(InstructionKey);
            await Assert.That(response.SessionInstructionsEntries.Single(entry => entry.Key == InstructionKey).Value.GetString()).IsEqualTo(InitialValue);
        }, cancellationToken);
    }

    [Test]
    [Timeout(60_000)]
    public async Task RemoveSessionInstructionsEntryAsync_Should_Hide_The_Entry_When_It_Exists(CancellationToken cancellationToken)
    {
        await WithInstructionEntryAsync(async (client, sessionId) =>
        {
            var before = await client.Experimental.ListSessionInstructionsEntriesAsync(sessionId, cancellationToken: cancellationToken);
            await Assert.That(before.SessionInstructionsEntries.Select(entry => entry.Key)).Contains(InstructionKey);

            _ = await client.Experimental.RemoveSessionInstructionsEntryAsync(sessionId, InstructionKey, cancellationToken: cancellationToken);

            var after = await client.Experimental.ListSessionInstructionsEntriesAsync(sessionId, cancellationToken: cancellationToken);
            await Assert.That(after.SessionInstructionsEntries.Select(entry => entry.Key)).DoesNotContain(InstructionKey);
        }, cancellationToken);
    }

    [Test]
    [Timeout(60_000)]
    public async Task SetSessionInstructionsEntryAsync_Should_Replace_The_Value_When_The_Key_Already_Exists(CancellationToken cancellationToken)
    {
        await WithInstructionEntryAsync(async (client, sessionId) =>
        {
            var before = await client.Experimental.ListSessionInstructionsEntriesAsync(sessionId, cancellationToken: cancellationToken);
            await Assert.That(before.SessionInstructionsEntries.Single(entry => entry.Key == InstructionKey).Value.GetString()).IsEqualTo(InitialValue);

            await SetInstructionAsync(client, sessionId, ReplacementValue, cancellationToken);

            var after = await client.Experimental.ListSessionInstructionsEntriesAsync(sessionId, cancellationToken: cancellationToken);
            await Assert.That(after.SessionInstructionsEntries.Single(entry => entry.Key == InstructionKey).Value.GetString()).IsEqualTo(ReplacementValue);
        }, cancellationToken);
    }

    private async Task WithInstructionEntryAsync(Func<OpenCodeClient, string, Task> scenario, CancellationToken token)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "experimental-instruction-live",
            Location = new LocationPublicRef { Directory = workspace.Path },
        }, cancellationToken: token);
        var sessionId = created.Session.Id;
        var session = client.Sessions.GetSessionClient(sessionId);
        var cleanup = new OwnedSessionCleanup(session, TimeSpan.FromSeconds(15));
        cleanup.MarkTurnCompleted();
        cleanup.Own("instruction entry removal", async cancellation =>
            _ = await client.Experimental.RemoveSessionInstructionsEntryAsync(sessionId, InstructionKey, cancellationToken: cancellation));
        Exception? primaryFailure = null;
        try
        {
            await SetInstructionAsync(client, sessionId, InitialValue, token);
            await scenario(client, sessionId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private static async Task SetInstructionAsync(OpenCodeClient client, string sessionId, string value, CancellationToken token) =>
        _ = await client.Experimental.SetSessionInstructionsEntryAsync(sessionId, InstructionKey,
            new ExperimentalSessionInstructionsEntryPutRequest { Value = DriveJson.Element(JsonValue.Create(value)) }, cancellationToken: token);

    private async Task WithMcpEntryAsync(Func<OpenCodeClient, string, SessionEventProbe, Task> scenario, CancellationToken token)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var name = "sdk-disabled-" + Guid.NewGuid().ToString("N");
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15));
        cleanup.Own("runtime MCP removal", async cancellation => await RemoveOwnedMcpAsync(client, name, cancellation));
        var reader = new OwnedEventReader(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(15), token);
        cleanup.Own("MCP event reader", TimeSpan.FromSeconds(75), _ => reader.CompleteAsync(null));
        var probe = new SessionEventProbe(reader);
        Exception? primaryFailure = null;
        try
        {
            probe.Start(client.Events.SubscribeAsync(cancellationToken: reader.Token));
            await probe.WaitForConnectedAsync(token);
            await scenario(client, name, probe);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private static async Task AddDisabledMcpAsync(OpenCodeClient client, string name, CancellationToken token) =>
        _ = await client.Experimental.AddMcpServerAsync(name, new ExperimentalMcpAddRequest
        {
            Config = new McpRemoteConfig
            {
                Disabled = true,
                OAuth = McpRemoteConfigOAuth.FromBoolean(false),
                Url = "https://sdk-coverage.invalid/mcp",
            },
        }, cancellationToken: token);

    private static async Task<McpStatusChanged> WaitForMcpChangeAsync(SessionEventProbe probe, string name, CancellationToken token)
    {
        using var barrier = SessionEventProbe.Barrier(token);
        return await probe.WaitForAsync<McpStatusChanged>(item => item.Data.Server == name, "owned disabled MCP entry", barrier.Token);
    }

    private static async Task AssertDisabledMcpAsync(OpenCodeClient client, string name, CancellationToken token)
    {
        var response = await client.McpServers.ListMcpServersAsync(cancellationToken: token);
        var entries = response.McpServers.Where(item => item.Name == name).ToList();
        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Status).IsTypeOf<McpStatusDisabled>();
    }

    private static async Task RemoveOwnedMcpAsync(OpenCodeClient client, string name, CancellationToken token)
    {
        var response = await client.Experimental.RemoveMcpServerAsync(name, requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: token);
        if (response.Status == 204 || (response is { Status: 404, Error: McpServerNotFoundError missing } && missing.Server == name))
        {
            return;
        }

        throw new InvalidOperationException("Owned MCP removal failed: " + response.Error?.GetType().Name);
    }
}
