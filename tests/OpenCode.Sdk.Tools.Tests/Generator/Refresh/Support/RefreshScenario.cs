using System.IO.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;

/// <summary>A captured accepted state and prepared document, composed through the production root.</summary>
internal sealed class RefreshScenario : IDisposable
{
    public const string CandidatePath = ".scratchpad/candidate.json";
    public const string ReceiptPath = ".scratchpad/receipt.json";
    public static readonly byte[] Document = RefreshScenarioData.DocumentBytes(spec => spec.WithOperation("test.get"));
    public MockFileSystem FileSystem { get; }
    public ScriptedProcessRunner Runner { get; }
    public ServiceProvider Services { get; }
    public SnapshotSynchronizer Synchronizer => Services.GetRequiredService<SnapshotSynchronizer>();
    public SnapshotState State { get; private set; } = null!;
    public SnapshotReceipt Receipt { get; private set; } = null!;

    public RefreshScenario(MockFileSystem? fileSystem = null, ScriptedProcessRunner? runner = null)
    {
        FileSystem = fileSystem ?? new MockFileSystem();
        Runner = runner ?? new ScriptedProcessRunner();
        Services = ToolApp.CreateServices(services =>
        {
            services.AddSingleton<IFileSystem>(FileSystem);
            services.AddSingleton<IProcessRunner>(Runner);
            services.AddSingleton<System.TimeProvider, FixedSnapshotTimeProvider>();
        }).BuildServiceProvider();
    }

    public static async Task<RefreshScenario> CreateAsync()
    {
        var scenario = new RefreshScenario { Receipt = RefreshScenarioData.Receipt(Document, CandidatePath) };
        var fs = scenario.FileSystem;
        _ = fs.Directory.CreateDirectory("spec");
        _ = fs.Directory.CreateDirectory(".scratchpad");
        _ = fs.Directory.CreateDirectory(SnapshotPaths.Submodule);
        await fs.File.WriteAllBytesAsync(SnapshotPaths.AcceptedDocument, Document);
        await fs.File.WriteAllBytesAsync(CandidatePath, Document);
        await fs.File.WriteAllTextAsync(SnapshotPaths.SnapshotMarkdown, RefreshScenarioData.SnapshotMarkdown);
        await scenario.WriteReceiptAsync(scenario.Receipt);
        scenario.ExpectHead();
        scenario.State = await scenario.Services.GetRequiredService<SnapshotStateReader>().ReadAsync(CancellationToken.None);
        return scenario;
    }

    public async Task WriteReceiptAsync(SnapshotReceipt receipt)
    {
        await FileSystem.File.WriteAllTextAsync(ReceiptPath, RefreshScenarioData.Serialize(receipt));
        await FileSystem.File.WriteAllTextAsync(SnapshotPaths.CommittedReceipt, RefreshScenarioData.Serialize(receipt));
    }

    public void ExpectHead() => Runner.Expect("git", ["rev-parse", "HEAD"], FileSystem.Path.GetFullPath(SnapshotPaths.Submodule),
        ScriptedProcessRunner.Ok(RefreshScenarioData.Commit));

    public void ExpectCommit() => Runner.Expect("git", ["rev-parse", "--verify", $"{RefreshScenarioData.Commit}^{{commit}}"],
        FileSystem.Path.GetFullPath(SnapshotPaths.Submodule));

    public ScriptedProcessRunner ExpectReference(byte[] raw, string reference = "origin/v2", string symbolic = "refs/remotes/origin/v2")
    {
        var directory = FileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        Runner.Expect("git", ["fetch", "origin"], directory);
        ExpectHead();
        return Runner.Expect("git", ["rev-parse", "--symbolic-full-name", reference], directory, ScriptedProcessRunner.Ok(symbolic))
            .Expect("git", ["rev-parse", $"{reference}^{{commit}}"], directory, ScriptedProcessRunner.Ok(RefreshScenarioData.Commit + "\n"))
            .Expect("git", ["show", $"{RefreshScenarioData.Commit}:{SnapshotPaths.UpstreamArtifact}"], directory, ScriptedProcessRunner.Ok(raw));
    }

    public async Task StageCandidateAsync(SnapshotReceipt receipt, byte[] bytes)
    {
        _ = FileSystem.Directory.CreateDirectory("scratch");
        await FileSystem.File.WriteAllBytesAsync("scratch/openapi.json", bytes);
        await FileSystem.File.WriteAllTextAsync("scratch/receipt.json", RefreshScenarioData.Serialize(receipt));
    }

    public async Task SeedAcceptedReceiptAsync(SnapshotReceipt receipt, byte[]? document = null)
    {
        if (document is not null)
        {
            await FileSystem.File.WriteAllBytesAsync(SnapshotPaths.AcceptedDocument, document);
        }

        await FileSystem.File.WriteAllTextAsync(SnapshotPaths.CommittedReceipt, RefreshScenarioData.Serialize(receipt));
    }

    public async Task<SnapshotRefreshException> PrepareRefusalAsync() =>
        (await Assert.That(async () => _ = await Synchronizer.PrepareAsync("origin/v2", CancellationToken.None))
            .Throws<SnapshotRefreshException>())!;

    public async Task<SnapshotRefreshException> ApplyRefusalAsync() =>
        (await Assert.That(async () => _ = await Synchronizer.ApplyAsync("scratch/receipt.json", CancellationToken.None))
            .Throws<SnapshotRefreshException>())!;

    public async Task SeedWatchedReceiptAsync(SnapshotReceipt receipt, WatchedSource pinned, bool anchorMatched, byte[] document)
    {
        await SeedAcceptedReceiptAsync(receipt with { WatchedSources = [RefreshScenarioData.Observation(pinned, pinned.Sha256, anchorMatched)] }, document);
        await FileSystem.File.WriteAllTextAsync(SnapshotPaths.SourceWatch, RefreshScenarioData.Serialize(RefreshScenarioData.Watch(1, pinned)));
    }

    public ScriptedProcessRunner ExpectObservedSource(WatchedSource pinned, string content)
    {
        ExpectHead();
        return Runner.Expect("git", ["show", $"{RefreshScenarioData.Commit}:{pinned.Path}"], FileSystem.Path.GetFullPath(SnapshotPaths.Submodule),
            ScriptedProcessRunner.Ok(content));
    }

    public ScriptedProcessRunner ExpectApply()
    {
        var directory = FileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        Runner.Expect("git", ["fetch", "origin"], directory);
        ExpectHead();
        ExpectCommit();
        return Runner.Expect("git", ["checkout", "--detach", RefreshScenarioData.Commit], directory);
    }

    public void ExpectUnpatchedGeneration()
    {
        var worktree = FileSystem.Path.GetFullPath(FileSystem.Path.Combine(SnapshotPaths.ScratchRoot, RefreshScenarioData.Commit, "worktree"));
        var artifact = FileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamArtifact);
        Runner.Expect("git", ["worktree", "add", "--detach", worktree, RefreshScenarioData.Commit], FileSystem.Path.GetFullPath(SnapshotPaths.Submodule));
        Runner.Expect("bun", ["install", "--frozen-lockfile", "--ignore-scripts"], worktree, sideEffect: () =>
        {
            _ = FileSystem.Directory.CreateDirectory(FileSystem.Path.GetDirectoryName(artifact)!);
            return Task.CompletedTask;
        });
        Runner.Expect("bun", ["run", "generate"], FileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage),
            sideEffect: () => FileSystem.File.WriteAllBytesAsync(artifact, Document));
        Runner.Expect("git", ["worktree", "remove", "--force", worktree], FileSystem.Path.GetFullPath(SnapshotPaths.Submodule));
    }

    public async Task<CommandAppResult> RunCommandAsync(params string[] arguments)
    {
        using var console = new TestConsole();
        _ = console.Width(200);
        using var registrar = ToolApp.CreateRegistrar(services =>
        {
            services.AddSingleton<IFileSystem>(FileSystem);
            services.AddSingleton<IProcessRunner>(Runner);
            services.AddSingleton<IAnsiConsole>(console);
            services.AddSingleton<System.TimeProvider, FixedSnapshotTimeProvider>();
        });
        var tester = new CommandAppTester(registrar, new CommandAppTesterSettings(), console);
        tester.Configure(ToolApp.Configure);
        return await tester.RunAsync(arguments);
    }

    public void WithWatch(string observedHash)
    {
        var pinned = RefreshScenarioData.Watched("watched.ts", "anchor", "anchor") with { Behavior = "the door's behavior" };
        var text = RefreshScenarioData.Serialize(RefreshScenarioData.Watch(1, pinned)).Replace("\\u0027", "'", StringComparison.Ordinal);
        State = State with { SourceWatch = System.Text.Encoding.UTF8.GetBytes(text) };
        Receipt = Receipt with { WatchedSources = [new ReceiptWatchedSource { Path = pinned.Path, Sha256 = observedHash, AnchorMatched = true }] };
    }

    public async Task<ReceiptPatch> AddPatchAsync(string name, int order)
    {
        _ = FileSystem.Directory.CreateDirectory(SnapshotPaths.PatchesRoot);
        var manifest = RefreshScenarioData.Manifest(sha256: DocumentInspector.Sha256Hex(Document)) with { Order = order };
        await FileSystem.File.WriteAllBytesAsync(FileSystem.Path.Combine(SnapshotPaths.PatchesRoot, manifest.Patch), Document);
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(SnapshotPaths.PatchesRoot, name), RefreshScenarioData.Serialize(manifest));
        return new ReceiptPatch { Manifest = name, PatchSha256 = manifest.Sha256, Preimages = [] };
    }

    public void Dispose() => Services.Dispose();
}
