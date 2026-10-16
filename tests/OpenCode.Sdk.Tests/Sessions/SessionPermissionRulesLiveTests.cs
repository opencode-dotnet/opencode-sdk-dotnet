using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// The session permission ruleset's live proof against the pinned server: the PATCH answers the
/// declared 204 and the session the rules were written to reports exactly the new ruleset back,
/// with nothing left of the one it was created with, so the operation is verified as a replacement
/// by the state it leaves rather than by its status alone.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionPermissionRulesLiveTests(SimulatedDriveServerFixture server)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The ruleset the session is created with, which the replacement must leave no trace of.</summary>
    private static readonly PermissionRule OriginalRule = new()
    {
        Action = SimulationConfigSeed.PermissionProbeAction,
        Resource = "sdk-live-original",
        Effect = PermissionEffect.Allow,
    };

    private static readonly PermissionRule ReplacementRule = new()
    {
        Action = SimulationConfigSeed.PermissionProbeAction,
        Resource = "sdk-live-denied",
        Effect = PermissionEffect.Deny,
    };

    [Test]
    [Timeout(120_000)]
    public async Task PutPermissionRulesAsync_Should_Replace_The_Session_Ruleset(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = "session-permission-rules-live",
                Location = new LocationPublicRef { Directory = workspace.Path },
                Permissions = new Optional<IReadOnlyList<PermissionRule>?>([OriginalRule]),
            },
            cancellationToken: cancellationToken);
        await Assert.That(created.Status).IsEqualTo(200);
        var session = client.Sessions.GetSessionClient(created.Session.Id);
        Exception? primaryFailure = null;

        try
        {
            await Assert.That(created.Session.Permissions).IsEquivalentTo([OriginalRule], CollectionOrdering.Matching);

            var response = await session.UpdateAsync(
                new SessionUpdateRequest
                {
                    Permissions = new Optional<IReadOnlyList<PermissionRule>?>([ReplacementRule]),
                },
                cancellationToken: cancellationToken);

            await Assert.That(response.Status).IsEqualTo(204);
            await Assert.That(response.IsError).IsFalse();

            var reread = await session.GetAsync(cancellationToken: cancellationToken);

            // Exact equality, as upstream's oracle asserts: a server or client that merged or
            // appended rules would still carry the original one.
            await Assert.That(reread.Status).IsEqualTo(200);
            await Assert.That(reread.Session.Permissions).IsEquivalentTo([ReplacementRule], CollectionOrdering.Matching);

            Console.WriteLine(
                "session-permission-rules-live: patch=" + Number(response.Status) +
                " reread=" + Number(reread.Status) +
                " rules=" + Number(reread.Session.Permissions!.Count));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await CleanupAsync(session, primaryFailure);
        }
    }

    private static async Task CleanupAsync(SessionClient session, Exception? primaryFailure)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            _ = await session.RemoveAsync(cancellationToken: cleanup.Token);
        }
        catch (Exception exception) when (primaryFailure is not null)
        {
            Console.WriteLine("session-permission-rules-live: cleanup suppressed " + exception.GetType().Name);
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
