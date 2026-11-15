using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class WatchedSourceReaderTests
{
    [Test]
    public async Task ObserveAsync_Should_Hash_The_Blob_And_Match_The_Anchor()
    {
        const string content = "const BUFFER_LIMIT = 1024 * 1024 * 2\n";
        var runner = new ScriptedProcessRunner()
            .Expect("git", ["show", "HEAD:packages/core/src/pty.ts"], new MockFileSystem().Path.GetFullPath(SnapshotPaths.Submodule), ScriptedProcessRunner.Ok(content));
        var reader = new WatchedSourceReader(new MockFileSystem(), runner);
        var pinned = RefreshScenarioData.Watched("packages/core/src/pty.ts", content, "BUFFER_LIMIT = 1024 * 1024 * 2");

        var observed = await reader.ObserveAsync("HEAD", [pinned], CancellationToken.None);

        await Assert.That(observed.Single().Sha256).IsEqualTo(pinned.Sha256);
        await Assert.That(observed.Single().Anchors.Single().Matched).IsTrue();
    }

    [Test]
    public async Task ObserveAsync_Should_Report_A_Lost_Anchor()
    {
        var pinned = RefreshScenarioData.Watched("packages/core/src/pty.ts", "const BUFFER_LIMIT = 2\n", "BUFFER_LIMIT = 1024");
        var runner = new ScriptedProcessRunner()
            .Expect("git", ["show", "HEAD:packages/core/src/pty.ts"], new MockFileSystem().Path.GetFullPath(SnapshotPaths.Submodule), ScriptedProcessRunner.Ok("const BUFFER_LIMIT = 2\n"));
        var reader = new WatchedSourceReader(new MockFileSystem(), runner);

        var observed = await reader.ObserveAsync("HEAD", [pinned], CancellationToken.None);

        await Assert.That(observed.Single().Anchors.Single().Matched).IsFalse();
        await Assert.That(observed.Single().Sha256).IsEqualTo(pinned.Sha256);
    }

    [Test]
    public async Task ObserveAsync_Should_Record_Each_Behavior_From_One_Blob_In_Watch_Order()
    {
        var pinned = RefreshScenarioData.PtyBufferSource with
        {
            Behaviors = [RefreshScenarioData.Behavior("zebra"), RefreshScenarioData.Behavior("absent", WatchOwners.AgentsAi),
                RefreshScenarioData.Behavior("alpha")],
        };
        var runner = new ScriptedProcessRunner().Expect("git", ["show", $"HEAD:{pinned.Path}"],
            new MockFileSystem().Path.GetFullPath(SnapshotPaths.Submodule), ScriptedProcessRunner.Ok("alpha zebra"));

        var observed = await new WatchedSourceReader(new MockFileSystem(), runner).ObserveAsync("HEAD", [pinned], CancellationToken.None);

        await Assert.That(observed.Single().Anchors.Select(static anchor => $"{anchor.Text}:{anchor.Matched}"))
            .IsEquivalentTo(["zebra:True", "absent:False", "alpha:True"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(runner.Invocations).IsEquivalentTo([$"git show HEAD:{pinned.Path}"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ObserveAsync_Should_Refuse_A_Source_The_Revision_Does_Not_Carry()
    {
        var runner = new ScriptedProcessRunner()
            .Expect("git", ["show", "HEAD:packages/core/src/pty.ts"], new MockFileSystem().Path.GetFullPath(SnapshotPaths.Submodule), ScriptedProcessRunner.Fail("fatal: path 'packages/core/src/pty.ts' does not exist"));
        var reader = new WatchedSourceReader(new MockFileSystem(), runner);
        var pinned = RefreshScenarioData.Watched("packages/core/src/pty.ts", "gone", "gone");

        var exception = await Assert
            .That(async () => _ = await reader.ObserveAsync("HEAD", [pinned], CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("cannot be read at HEAD");
    }

    [Test]
    public async Task ObserveAsync_Should_Observe_Nothing_For_An_Empty_Watch()
    {
        var runner = new ScriptedProcessRunner();
        var reader = new WatchedSourceReader(new MockFileSystem(), runner);

        var observed = await reader.ObserveAsync("HEAD", [], CancellationToken.None);

        await Assert.That(observed).IsEmpty();
        await Assert.That(runner.Invocations).IsEmpty();
    }
}
