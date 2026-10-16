using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Owns a live worktree scenario's Git repository workspace from creation to teardown: the
/// linked worktree is removed through the SDK at its known owned destination (never at an
/// unchecked server-returned path), then the workspace is disposed, each under its own finite
/// budget, with the primary failure and every cleanup failure preserved.
/// </summary>
internal sealed class OwnedWorktreeScenario
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    private readonly OpenCodeClient _client;
    private readonly OwnedCleanup _cleanup = new(CleanupTimeout);
    private GitRepositoryWorkspace? _repository;

    private OwnedWorktreeScenario(OpenCodeClient client)
    {
        _client = client;
        _cleanup.Own("Git worktree removal", RemoveIfPresentAsync);
        _cleanup.Own("Git repository workspace", _ =>
        {
            Repository.Dispose();
            return Task.CompletedTask;
        });
    }

    public string ProjectId { get; private set; } = string.Empty;

    public GitRepositoryWorkspace Repository =>
        _repository ?? throw new InvalidOperationException("The worktree scenario has not initialized.");

    /// <summary>Creates the owned repository and seeds the worktree destination's parent marker.</summary>
    public static async Task<OwnedWorktreeScenario> CreateAsync(
        SimulatedDriveServerFixture server,
        OpenCodeClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);

        var scenario = new OwnedWorktreeScenario(client);
        await scenario.InitializeAsync(server, cancellationToken);
        return scenario;
    }

    public Task CompleteAsync(Exception? primaryFailure) => _cleanup.CompleteAsync(primaryFailure);

    private async Task InitializeAsync(SimulatedDriveServerFixture server, CancellationToken cancellationToken)
    {
        // The repository is owned by this scenario from the moment it exists; a failure to seed
        // the destination disposes it here, since no caller ever receives the scenario.
        _repository = await server.CreateGitRepositoryWorkspaceAsync(cancellationToken);
        try
        {
            _repository.PrepareWorktreeDestination();
            var resolved = await _client.GetLocationAsync(
                new LocationRequest { Location = _repository.Location }, cancellationToken: cancellationToken);
            var location = resolved.ResolvedLocation;
            if (!_repository.OwnsDirectory(location.Directory)
                || !_repository.OwnsDirectory(location.Project.Directory)
                || !_repository.OwnsDirectory(location.Project.Canonical))
            {
                throw new InvalidOperationException("The worktree project did not resolve to the owned repository.");
            }

            ProjectId = location.Project.Id;
        }
        catch
        {
            _repository.Dispose();
            _repository = null;
            throw;
        }
    }

    private async Task RemoveIfPresentAsync(CancellationToken cancellationToken)
    {
        if (!Repository.WorktreeExists)
        {
            return;
        }

        var response = await _client.Worktrees.RemoveWorktreeAsync(
            new WorktreeRemoveRequest
            {
                ProjectId = ProjectId,
                Directory = Repository.ExpectedWorktreePath,
                Force = true,
            },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);
        if (response.Status != 204 || response.IsError)
        {
            throw new InvalidOperationException(
                "Owned worktree removal returned status " + response.Status.ToString(CultureInfo.InvariantCulture) +
                ", body " + response.RawBody + ".");
        }
    }
}
