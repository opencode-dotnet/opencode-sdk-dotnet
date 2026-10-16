using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Config;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ConfigClientLiveTests(SimulatedDriveServerFixture server)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);

    [Test]
    [Timeout(60_000)]
    public async Task GetShellsAsync_Should_Report_Acceptable_Host_Shells(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        var response = await client.Config.GetShellsAsync(cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Shells).IsNotEmpty();
        foreach (var shell in response.Shells)
        {
            await Assert.That(shell.Path).IsNotEmpty();
            await Assert.That(shell.Name).IsNotEmpty();
        }

        await Assert.That(response.Shells.Any(static shell => shell.Acceptable)).IsTrue();

        Console.WriteLine(
            "config-live: shells status=" + Number(response.Status) +
            " count=" + Number(response.Shells.Count) +
            " acceptable=" + Number(response.Shells.Count(static shell => shell.Acceptable)));
    }

    [Test]
    [Timeout(60_000)]
    public async Task UpdateConfigAsync_Should_Persist_And_Clear_The_Shell(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        var shells = await client.Config.GetShellsAsync(cancellationToken: cancellationToken);
        var shell = shells.Shells.First(static candidate => candidate.Acceptable).Path;
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(10));
        var cleared = false;
        Exception? failure = null;
        cleanup.Own("clear isolated shell config", async token =>
        {
            if (!cleared)
            {
                _ = await client.Experimental.UpdateConfigAsync(
                    new ExperimentalConfigUpdateRequest { Shell = null, }, cancellationToken: token);
            }
        });
        try
        {
            var set = await client.Experimental.UpdateConfigAsync(
                new ExperimentalConfigUpdateRequest { Shell = shell, }, cancellationToken: cancellationToken);
            await Assert.That(set.Status).IsEqualTo(204);
            var jsonOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            };
            using var afterSet = JsonDocument.Parse(await server.ReadGlobalConfigAsync(cancellationToken), jsonOptions);
            await Assert.That(afterSet.RootElement.GetProperty("shell").GetString()).IsEqualTo(shell);

            var clear = await client.Experimental.UpdateConfigAsync(
                new ExperimentalConfigUpdateRequest { Shell = null, }, cancellationToken: cancellationToken);
            await Assert.That(clear.Status).IsEqualTo(204);
            using var afterClear = JsonDocument.Parse(await server.ReadGlobalConfigAsync(cancellationToken), jsonOptions);
            await Assert.That(afterClear.RootElement.TryGetProperty("shell", out _)).IsFalse();
            cleared = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await cleanup.CompleteAsync(failure);
    }

    /// <summary>
    /// <c>config.get</c> answers the location's loaded configuration, and <c>experimental.config.update</c>
    /// writes the global file and only requests a reload (debounced, asynchronous) that publishes
    /// <c>config.updated</c> once the loaded entries change. So the read-back waits for that event,
    /// never for a clock, and reads again after each one until the write shows.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task GetConfigAsync_Should_Read_Back_The_Shell_UpdateConfig_Wrote(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        var shells = await client.Config.GetShellsAsync(cancellationToken: cancellationToken);
        var shell = shells.Shells.First(static candidate => candidate.Acceptable).Path;
        var reader = new OwnedEventReader(EventWait, CleanupTimeout, cancellationToken);
        var probe = new SessionEventProbe(reader);
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(10));
        var cleared = false;
        Exception? failure = null;
        cleanup.Own("clear isolated shell config", async token =>
        {
            if (!cleared)
            {
                _ = await client.Experimental.UpdateConfigAsync(
                    new ExperimentalConfigUpdateRequest { Shell = null, }, cancellationToken: token);
            }
        });
        try
        {
            probe.Start(client.Events.SubscribeAsync(reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);

            var set = await client.Experimental.UpdateConfigAsync(
                new ExperimentalConfigUpdateRequest { Shell = shell, }, cancellationToken: cancellationToken);
            await Assert.That(set.Status).IsEqualTo(204);
            var reloaded = await WaitForShellsAsync(client, probe, after: null, shells => shells.Contains(shell, StringComparer.Ordinal), "the written shell", cancellationToken);

            var clear = await client.Experimental.UpdateConfigAsync(
                new ExperimentalConfigUpdateRequest { Shell = null, }, cancellationToken: cancellationToken);
            await Assert.That(clear.Status).IsEqualTo(204);
            _ = await WaitForShellsAsync(client, probe, reloaded, shells => !shells.Contains(shell, StringComparer.Ordinal), "the cleared shell", cancellationToken);
            cleared = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            if (!exception.Data.Contains(EventDiagnosticSummary.DataKey))
            {
                exception.Data[EventDiagnosticSummary.DataKey] = probe.DiagnosticSummary;
            }
        }
        finally
        {
            await reader.CompleteAsync(failure);
        }

        await cleanup.CompleteAsync(failure);
    }

    /// <summary>
    /// Waits for each <c>config.updated</c> after <paramref name="after"/> and reads the shells
    /// the location's configuration documents declare, until <paramref name="expected"/> holds;
    /// returns the event that preceded the matching read, the anchor for the next wait.
    /// </summary>
    private static async Task<ConfigUpdated> WaitForShellsAsync(OpenCodeClient client, SessionEventProbe probe, IEvent? after,
        Func<IReadOnlyList<string>, bool> expected, string description, CancellationToken cancellationToken)
    {
        using var barrier = SessionEventProbe.Barrier(cancellationToken);
        while (true)
        {
            var updated = await probe.WaitForAsync<ConfigUpdated>(static _ => true, "config.updated for " + description, after, barrier.Token);
            var response = await client.Config.GetConfigAsync(cancellationToken: cancellationToken);
            await Assert.That(response.Status).IsEqualTo(200);
            IReadOnlyList<string> shells = [.. response.Config.OfType<ConfigDocument>().Select(static document => document.Info.Shell).OfType<string>()];
            if (expected(shells))
            {
                return updated;
            }

            after = updated;
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
