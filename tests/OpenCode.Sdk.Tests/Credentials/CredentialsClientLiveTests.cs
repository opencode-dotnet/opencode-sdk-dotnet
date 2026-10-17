using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;

namespace OpenCode.Sdk.Tests.Credentials;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class CredentialsClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string InitialLabel = "Owned coverage credential";
    private const string ReplacementLabel = "Renamed owned credential";
    private readonly string _integrationId = "sdk-credential-" + Guid.NewGuid().ToString("N");

    [Test]
    [Timeout(60_000)]
    public async Task RemoveCredentialAsync_Should_Remove_The_Owned_Row_When_It_Exists(CancellationToken cancellationToken)
    {
        await WithCredentialsAsync(async (client, cleanup) =>
        {
            var id = await CreateOwnedAsync(client, cleanup, InitialLabel, cancellationToken);
            var before = await ListOwnedAsync(client, [id], cancellationToken);
            await Assert.That(before.Select(entry => entry.Id)).Contains(id);

            _ = await client.Credentials.RemoveCredentialAsync(id, cancellationToken: cancellationToken);

            var after = await ListOwnedAsync(client, [id], cancellationToken);
            await Assert.That(after.Select(entry => entry.Id)).DoesNotContain(id);
        });
    }

    [Test]
    [Timeout(60_000)]
    public async Task CreateCredentialAsync_Should_Persist_The_Row_When_The_Id_Is_Unused(CancellationToken cancellationToken)
    {
        await WithCredentialsAsync(async (client, cleanup) =>
        {
            var (request, id) = OwnCredential(client, cleanup, InitialLabel);

            _ = await client.Credentials.CreateCredentialAsync(request, cancellationToken: cancellationToken);

            _ = await AssertOwnedRowAsync(client, id, InitialLabel, cancellationToken);
        });
    }

    [Test]
    [Timeout(60_000)]
    public async Task ActivateCredentialAsync_Should_Select_The_Older_Row_When_A_Newer_Row_Is_Active(CancellationToken cancellationToken)
    {
        await WithCredentialsAsync(async (client, cleanup) =>
        {
            var olderId = await CreateOwnedAsync(client, cleanup, InitialLabel, cancellationToken);
            var newerId = await CreateOwnedAsync(client, cleanup, ReplacementLabel, cancellationToken);
            var before = await ListOwnedAsync(client, [olderId, newerId], cancellationToken);
            await Assert.That(before.Single(entry => entry.Id == olderId).Active).IsFalse();
            await Assert.That(before.Single(entry => entry.Id == newerId).Active).IsTrue();

            _ = await client.Credentials.ActivateCredentialAsync(olderId, cancellationToken: cancellationToken);

            var after = await ListOwnedAsync(client, [olderId, newerId], cancellationToken);
            await Assert.That(after.Single(entry => entry.Id == olderId).Active).IsTrue();
            await Assert.That(after.Single(entry => entry.Id == newerId).Active).IsFalse();
        });
    }

    [Test]
    [Timeout(60_000)]
    public async Task ListCredentialsAsync_Should_Return_The_Owned_Metadata_When_A_Row_Was_Created(CancellationToken cancellationToken)
    {
        await WithCredentialsAsync(async (client, cleanup) =>
        {
            var id = await CreateOwnedAsync(client, cleanup, InitialLabel, cancellationToken);

            var entry = await AssertOwnedRowAsync(client, id, InitialLabel, cancellationToken);
            await Assert.That(entry.Active).IsTrue();
        });
    }

    [Test]
    [Timeout(60_000)]
    public async Task UpdateCredentialAsync_Should_Replace_The_Label_When_A_Row_Exists(CancellationToken cancellationToken)
    {
        await WithCredentialsAsync(async (client, cleanup) =>
        {
            var id = await CreateOwnedAsync(client, cleanup, InitialLabel, cancellationToken);

            _ = await client.Credentials.UpdateCredentialAsync(id,
                new CredentialUpdateRequest { Label = ReplacementLabel }, cancellationToken: cancellationToken);

            var owned = await ListOwnedAsync(client, [id], cancellationToken);
            await Assert.That(owned.Single().Label).IsEqualTo(ReplacementLabel);
        });
    }

    private async Task WithCredentialsAsync(Func<OpenCodeClient, OwnedCleanup, Task> scenario)
    {
        using var workspace = server.CreateWorkspace();
        using var client = CreateWorkspaceClient(workspace);
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15));
        Exception? failure = null;
        try
        {
            await scenario(client, cleanup);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await cleanup.CompleteAsync(failure);
    }

    private OpenCodeClient CreateWorkspaceClient(TestWorkspace workspace) =>
        server.CreateClient(new LocationSelector { Directory = workspace.Path });

    private (CredentialCreateRequest Request, string Id) OwnCredential(OpenCodeClient client, OwnedCleanup cleanup, string label)
    {
        var id = "cred_" + Guid.NewGuid().ToString("N");
        cleanup.Own("owned credential removal", async token =>
            _ = await client.Credentials.RemoveCredentialAsync(id, cancellationToken: token));
        return (new CredentialCreateRequest
        {
            Id = id,
            IntegrationId = _integrationId,
            Label = label,
            Value = new CredentialKey { Key = "inert-sdk-coverage-sentinel" },
        }, id);
    }

    private async Task<string> CreateOwnedAsync(OpenCodeClient client, OwnedCleanup cleanup, string label, CancellationToken token)
    {
        var (request, id) = OwnCredential(client, cleanup, label);
        _ = await client.Credentials.CreateCredentialAsync(request, cancellationToken: token);
        return id;
    }

    private async Task<CredentialEntry> AssertOwnedRowAsync(OpenCodeClient client, string id, string label, CancellationToken token)
    {
        var owned = await ListOwnedAsync(client, [id], token);
        await Assert.That(owned.Select(entry => entry.Id)).Contains(id);
        var entry = owned.Single();
        await Assert.That(entry.IntegrationId).IsEqualTo(_integrationId);
        await Assert.That(entry.Label).IsEqualTo(label);
        return entry;
    }

    private static async Task<IReadOnlyList<CredentialEntry>> ListOwnedAsync(OpenCodeClient client, IEnumerable<string> ids, CancellationToken token)
    {
        var response = await client.Credentials.ListCredentialsAsync(cancellationToken: token);
        // Filter before inspecting metadata; never inspect or assert a credential's value.
        return [.. response.Credentials.Where(entry => ids.Contains(entry.Id, StringComparer.Ordinal))];
    }
}
