using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Worktrees;

/// <summary>
/// The worktree family's live proof against the simulated server over an owned local Git
/// repository: the root row, a linked Git worktree created under an owned parent with a fixed
/// absent name, refresh preserving the inventory, the dirty-removal refusal that names force,
/// and forced removal. Physical ownership of every
/// server-returned directory is proven through independently seeded markers, never through
/// the spelling of a temporary path.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class WorktreesClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string GitStrategy = "git";

    [Test]
    [Timeout(60_000)]
    public async Task CreateWorktreeAsync_And_RefreshWorktreesAsync_Should_Preserve_The_Project_Inventory(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        var scenario = await OwnedWorktreeScenario.CreateAsync(server, client, cancellationToken);
        var repository = scenario.Repository;
        Exception? failure = null;
        try
        {
            var request = new WorktreeListRequest { ProjectId = scenario.ProjectId };
            var before = await client.Worktrees.ListWorktreesAsync(request, cancellationToken: cancellationToken);
            await Assert.That(before.Status).IsEqualTo(200);
            await Assert.That(before.IsError).IsFalse();
            var root = before.Worktrees.Single();
            await Assert.That(repository.OwnsDirectory(root.Directory)).IsTrue();
            await Assert.That(root.Strategy).IsNull();

            var created = await CreateLinkedWorktreeAsync(client, repository, scenario.ProjectId, cancellationToken);

            var after = await client.Worktrees.ListWorktreesAsync(request, cancellationToken: cancellationToken);
            await Assert.That(after.Status).IsEqualTo(200);
            await Assert.That(after.IsError).IsFalse();
            await Assert.That(after.Worktrees.Count).IsEqualTo(2);
            await Assert.That(after.Worktrees.Single(row => row.Directory == root.Directory).Strategy).IsNull();
            await Assert.That(after.Worktrees.Single(row => row.Directory == created.Worktree.Directory).Strategy)
                .IsEqualTo(GitStrategy);

            var refreshed = await client.Worktrees.RefreshWorktreesAsync(
                new WorktreeRefreshRequest { ProjectId = scenario.ProjectId },
                cancellationToken: cancellationToken);
            await Assert.That(refreshed.Status).IsEqualTo(204);
            await Assert.That(refreshed.IsError).IsFalse();

            var final = await client.Worktrees.ListWorktreesAsync(request, cancellationToken: cancellationToken);
            await Assert.That(final.Status).IsEqualTo(200);
            await Assert.That(final.IsError).IsFalse();
            await Assert.That(final.Worktrees).IsEquivalentTo(after.Worktrees);

            Console.WriteLine(
                "worktree-live: mode=owned arm=list-create-list-refresh-list status=" +
                Number(before.Status) + "/" + Number(created.Status) + "/" + Number(after.Status) + "/" +
                Number(refreshed.Status) + "/" + Number(final.Status));
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await scenario.CompleteAsync(failure);
    }

    [Test]
    [Timeout(60_000)]
    public async Task RemoveWorktreeAsync_Should_Require_Force_For_A_Dirty_Worktree(
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        var scenario = await OwnedWorktreeScenario.CreateAsync(server, client, cancellationToken);
        var repository = scenario.Repository;
        Exception? failure = null;
        try
        {
            var created = await CreateLinkedWorktreeAsync(client, repository, scenario.ProjectId, cancellationToken);
            repository.WriteDirtyWorktreeFile();

            var remove = new WorktreeRemoveRequest
            {
                ProjectId = scenario.ProjectId,
                Directory = created.Worktree.Directory,
                Force = false,
            };
            var refused = await client.Worktrees.RemoveWorktreeAsync(
                remove, OpenCodeRequestOptions.NoThrow, cancellationToken);
            var error = await RequireWorktreeErrorAsync(refused.Status, refused.IsError, refused.Error);
            await Assert.That(error.Data.ForceRequired).IsTrue();
            await Assert.That(string.IsNullOrWhiteSpace(error.Data.Message)).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(refused.RawBody)).IsFalse();
            await Assert.That(repository.WorktreeExists).IsTrue();
            await Assert.That(repository.DirtyWorktreeFileExists).IsTrue();

            var removed = await client.Worktrees.RemoveWorktreeAsync(
                remove with { Force = true }, cancellationToken: cancellationToken);
            await Assert.That(removed.Status).IsEqualTo(204);
            await Assert.That(removed.IsError).IsFalse();

            var final = await client.Worktrees.ListWorktreesAsync(
                new WorktreeListRequest { ProjectId = scenario.ProjectId }, cancellationToken: cancellationToken);
            await Assert.That(final.Status).IsEqualTo(200);
            await Assert.That(final.IsError).IsFalse();
            var root = final.Worktrees.Single();
            await Assert.That(repository.OwnsDirectory(root.Directory)).IsTrue();
            await Assert.That(root.Strategy).IsNull();
            await Assert.That(repository.WorktreeExists).IsFalse();
            await Assert.That(repository.DirtyWorktreeFileExists).IsFalse();

            Console.WriteLine(
                "worktree-live: mode=owned arm=dirty-remove-refused/forced status=" + Number(refused.Status) + "/" +
                Number(removed.Status) + "/" + Number(final.Status) + " message=" + error.Data.Message);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await scenario.CompleteAsync(failure);
    }

    /// <summary>
    /// Creates the linked Git worktree and proves the server created exactly the intended child:
    /// the fixed name is observed absent first, the returned directory resolves to the owned
    /// parent with that name, and the child physically exists afterwards.
    /// </summary>
    private static async Task<WorktreeCreateResponse> CreateLinkedWorktreeAsync(
        OpenCodeClient client,
        GitRepositoryWorkspace repository,
        string projectId,
        CancellationToken cancellationToken)
    {
        await Assert.That(repository.WorktreeExists).IsFalse();
        var created = await client.Worktrees.CreateWorktreeAsync(
            CreateRequest(repository, projectId), cancellationToken: cancellationToken);
        await Assert.That(created.Status).IsEqualTo(200);
        await Assert.That(created.IsError).IsFalse();
        await Assert.That(repository.OwnsWorktreeDirectory(created.Worktree.Directory)).IsTrue();
        await Assert.That(repository.WorktreeExists).IsTrue();
        return created;
    }

    /// <summary>The declared 400 <see cref="WorktreeError"/> arm, or a failure naming the arm that arrived instead.</summary>
    private static async Task<WorktreeError> RequireWorktreeErrorAsync(int status, bool isError, IOpenCodeError? error)
    {
        await Assert.That(status).IsEqualTo(400);
        await Assert.That(isError).IsTrue();
        await Assert.That(error).IsTypeOf<WorktreeError>();
        return error as WorktreeError ?? throw new InvalidOperationException("The WorktreeError arm was absent.");
    }

    /// <summary>
    /// Every create names its project, its owned parent, and its fixed absent child name, so
    /// nothing is written under server-owned data.
    /// </summary>
    private static WorktreeCreateRequest CreateRequest(GitRepositoryWorkspace repository, string projectId) => new()
    {
        ProjectId = projectId,
        Directory = repository.WorktreeParentPath,
        Name = GitRepositoryWorkspace.WorktreeName,
    };

    /// <summary>Renders one number for the console line, culture-free.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
