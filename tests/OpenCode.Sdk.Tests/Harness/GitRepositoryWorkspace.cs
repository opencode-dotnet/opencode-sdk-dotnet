using System.IO.Abstractions;
using OpenCode.Sdk.Tests.Harness.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class GitRepositoryWorkspace : IDisposable
{
    internal const string CleanupFailuresKey = "GitRepositoryWorkspace.CleanupFailures";
    private const string InitialContent = "before\n";
    private const string ModifiedContent = "after\n";
    private const string OwnerMarker = ".git/opencode-sdk-test-owner.txt";
    private const string TrackedFile = "tracked.txt";

    /// <summary>
    /// The linked worktree's destination lives beside the repository, under an owned parent
    /// carrying its own marker: a linked worktree's <c>.git</c> is a file, so the repository's
    /// marker cannot prove a child, and the parent marker stays outside the working-file
    /// inventory the VCS proofs assert on.
    /// </summary>
    private const string WorktreeParentMarker = ".opencode-sdk-test-owner.txt";
    private const string WorktreeParentName = "worktrees";
    private const string LinkedWorktreeName = "linked";
    private const string DirtyWorktreeFile = "dirty.txt";
    private const string InitializeArguments =
        "-c core.hooksPath=../hooks init --initial-branch=main --template=../template";
    private const string AddArguments =
        "-c core.hooksPath=../hooks -c core.autocrlf=false -c core.fsmonitor=false add -- tracked.txt";
    private const string CommitArguments =
        "-c core.hooksPath=../hooks -c core.autocrlf=false -c core.fsmonitor=false " +
        "-c user.name=OpenCodeSdkTest -c user.email=opencode-sdk@test.invalid " +
        "-c commit.gpgSign=false commit --no-verify -m initial";

    private readonly IFileSystem _fileSystem;
    private readonly IGitProcess _gitProcess;
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly string _runRoot;
    private TestWorkspace? _workspace;

    internal GitRepositoryWorkspace(IFileSystem fileSystem, IGitProcess gitProcess, string runRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(gitProcess);
        ArgumentException.ThrowIfNullOrWhiteSpace(runRoot);

        _fileSystem = fileSystem;
        _gitProcess = gitProcess;
        _runRoot = runRoot;
    }

    public LocationSelector Location => new() { Directory = RepositoryPath };

    internal string RepositoryPath => _fileSystem.Path.Combine(Workspace.Path, "repository");

    /// <summary>The owned parent a linked worktree is created under.</summary>
    public string WorktreeParentPath => _fileSystem.Path.Combine(Workspace.Path, WorktreeParentName);

    /// <summary>The fixed child name every create names; absent until the server creates it.</summary>
    public const string WorktreeName = LinkedWorktreeName;

    /// <summary>The logical destination the linked worktree is expected at, for cleanup and existence checks.</summary>
    public string ExpectedWorktreePath => _fileSystem.Path.Combine(WorktreeParentPath, LinkedWorktreeName);

    public bool WorktreeExists => _fileSystem.Directory.Exists(ExpectedWorktreePath);

    public bool DirtyWorktreeFileExists =>
        _fileSystem.File.Exists(_fileSystem.Path.Combine(ExpectedWorktreePath, DirtyWorktreeFile));

    private TestWorkspace Workspace =>
        _workspace ?? throw new InvalidOperationException("The Git repository workspace has not initialized.");

    internal static async Task<GitRepositoryWorkspace> CreateAsync(
        IFileSystem fileSystem,
        IGitProcess gitProcess,
        string runRoot,
        CancellationToken cancellationToken)
    {
        var workspace = new GitRepositoryWorkspace(fileSystem, gitProcess, runRoot);
        try
        {
            await workspace.InitializeAsync(cancellationToken);
            return workspace;
        }
        catch (Exception initializationFailure)
        {
            try
            {
                workspace.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                initializationFailure.Data[CleanupFailuresKey] = new AggregateException(cleanupFailure);
            }

            throw;
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_workspace is not null)
        {
            throw new InvalidOperationException("The Git repository workspace has already initialized.");
        }

        _workspace = new TestWorkspace(_fileSystem, _runRoot);
        _ = _fileSystem.Directory.CreateDirectory(RepositoryPath);
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.Combine(Workspace.Path, "hooks"));
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.Combine(Workspace.Path, "template"));
        _ = Workspace.WriteTextFile("repository/" + TrackedFile, InitialContent);
        await _gitProcess.RunAsync(RepositoryPath, InitializeArguments, cancellationToken);
        _ = Workspace.WriteTextFile("repository/" + OwnerMarker, _owner);
        await _gitProcess.RunAsync(RepositoryPath, AddArguments, cancellationToken);
        await _gitProcess.RunAsync(RepositoryPath, CommitArguments + "-" + _owner, cancellationToken);
    }

    public bool OwnsDirectory(string directory) => Workspace.HasTextFile(directory, OwnerMarker, _owner);

    /// <summary>Seeds the owned parent's marker; the child itself stays absent for the server to create.</summary>
    public void PrepareWorktreeDestination() =>
        _ = Workspace.WriteTextFile(WorktreeParentName + "/" + WorktreeParentMarker, _owner);

    /// <summary>
    /// Proves a server-returned directory is the intended child: its physical parent carries the
    /// seeded marker and its name is the fixed child name. This says nothing about whether the
    /// child exists; <see cref="WorktreeExists"/> does.
    /// </summary>
    public bool OwnsWorktreeDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var parent = _fileSystem.Path.GetDirectoryName(directory);
        return parent is not null
            && string.Equals(_fileSystem.Path.GetFileName(directory), LinkedWorktreeName, StringComparison.Ordinal)
            && Workspace.HasTextFile(parent, WorktreeParentMarker, _owner);
    }

    /// <summary>Dirties the created worktree at its known path; refuses to invent a checkout that does not exist.</summary>
    public void WriteDirtyWorktreeFile()
    {
        if (!WorktreeExists)
        {
            throw new InvalidOperationException("The owned worktree does not exist.");
        }

        _ = Workspace.WriteTextFile(
            WorktreeParentName + "/" + LinkedWorktreeName + "/" + DirtyWorktreeFile, "dirty\n");
    }

    public void WriteModifiedTrackedFile() =>
        _ = Workspace.WriteTextFile("repository/" + TrackedFile, ModifiedContent);

    public void Dispose() => _workspace?.Dispose();
}
