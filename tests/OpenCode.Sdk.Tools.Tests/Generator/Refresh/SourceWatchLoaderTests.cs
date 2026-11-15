using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class SourceWatchLoaderTests
{
    [Test]
    public async Task LoadAsync_Should_Use_Schema_Two_For_An_Absent_Watch()
    {
        var watch = await new SourceWatchLoader(new MockFileSystem()).LoadAsync(CancellationToken.None);

        await Assert.That(watch.SchemaVersion).IsEqualTo(2);
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Null_Sources_Member()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory("spec");
        await fileSystem.File.WriteAllTextAsync(SnapshotPaths.SourceWatch,
            RefreshScenarioData.WithNullMember(RefreshScenarioData.Serialize(RefreshScenarioData.Watch(2)), "sources"));

        var exception = await Assert.That(async () => _ = await new SourceWatchLoader(fileSystem)
            .LoadAsync(CancellationToken.None)).Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).StartsWith("the source watch 'spec/source-watch.json' is invalid:");
        await Assert.That(exception.Message).Contains("sources");
    }

    [Test]
    public async Task LoadAsync_Should_Return_An_Empty_Watch_When_No_File_Exists()
    {
        var loader = new SourceWatchLoader(new MockFileSystem());

        var watch = await loader.LoadAsync(CancellationToken.None);

        await Assert.That(watch.Sources).IsEmpty();
    }

    [Test]
    public async Task LoadAsync_Should_Read_Every_Pinned_Source()
    {
        var loader = await CreateLoaderAsync(RefreshScenarioData.Watch(
            2,
            RefreshScenarioData.Watched("packages/server/src/handlers/pty.ts", "close(4404)", "4404"),
            RefreshScenarioData.Watched("packages/core/src/pty/ticket.ts", "Duration.seconds(60)", "Duration.seconds(60)")));

        var watch = await loader.LoadAsync(CancellationToken.None);

        // This checks the complete inventory, not its enumeration order.
        await Assert.That(watch.Sources.Select(static source => source.Path))
            .IsEquivalentTo(["packages/server/src/handlers/pty.ts", "packages/core/src/pty/ticket.ts"], CollectionOrdering.Any);
        await Assert.That(watch.Sources[0].Behaviors.Single().Anchor.Text).IsEqualTo("4404");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_An_Unsupported_Schema_Version()
    {
        var loader = await CreateLoaderAsync(RefreshScenarioData.Watch(
            1, RefreshScenarioData.Watched("packages/core/src/pty.ts", "BUFFER_LIMIT", "BUFFER_LIMIT")));

        var exception = await Assert
            .That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("schema version 1");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_An_Unsupported_Anchor_Type()
    {
        var loader = await CreateLoaderAsync(RefreshScenarioData.Watch(
            2, RefreshScenarioData.Watched("packages/core/src/pty.ts", "BUFFER_LIMIT", "BUFFER_LIMIT", anchorType: "matches")));

        var exception = await Assert
            .That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("unsupported anchor type 'matches'");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Malformed_Pin()
    {
        var source = RefreshScenarioData.Watched("packages/core/src/pty.ts", "BUFFER_LIMIT", "BUFFER_LIMIT");
        var loader = await CreateLoaderAsync(RefreshScenarioData.Watch(2, source with { Sha256 = "not-a-hash" }));

        var exception = await Assert
            .That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("does not pin a lowercase SHA-256");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Duplicated_Path()
    {
        var source = RefreshScenarioData.Watched("packages/core/src/pty.ts", "BUFFER_LIMIT", "BUFFER_LIMIT");
        var loader = await CreateLoaderAsync(RefreshScenarioData.Watch(2, source, source));

        var exception = await Assert
            .That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("more than once");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Watch_Without_Sources()
    {
        var loader = await CreateLoaderAsync(RefreshScenarioData.Watch(2));

        var exception = await Assert
            .That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("names no sources");
    }

    [Test]
    [Arguments("empty", "must name at least one behavior")]
    [Arguments("owner", "declares unsupported owner 'other' for anchor 'second'")]
    [Arguments("blank owner", "declares unsupported owner '' for anchor 'second'")]
    [Arguments("behavior", "must state the behavior for anchor 'second'")]
    [Arguments("type", "declares unsupported anchor type 'matches' for anchor 'second'")]
    [Arguments("text", "declares an empty anchor")]
    [Arguments("duplicate", "names anchor 'first' more than once")]
    public async Task LoadAsync_Should_Refuse_Invalid_Behaviors(string fault, string diagnostic)
    {
        var first = RefreshScenarioData.Behavior("first");
        var second = RefreshScenarioData.Behavior("second", WatchOwners.AgentsAi);
        second = fault switch
        {
            "owner" => second with { Owner = "other" },
            "blank owner" => second with { Owner = string.Empty },
            "behavior" => second with { Behavior = " " },
            "type" => second with { Anchor = second.Anchor with { Type = "matches" } },
            "text" => second with { Anchor = second.Anchor with { Text = " " } },
            "duplicate" => second with { Anchor = first.Anchor },
            _ => second,
        };
        var source = RefreshScenarioData.PtyBufferSource with { Behaviors = fault == "empty" ? [] : [first, second] };
        var loader = await CreateLoaderAsync(RefreshScenarioData.Watch(2, source));

        var exception = await Assert.That(async () => _ = await loader.LoadAsync(CancellationToken.None)).Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).IsEqualTo($"the source watch entry '{source.Path}' {diagnostic}");
    }

    private static async Task<SourceWatchLoader> CreateLoaderAsync(SourceWatch watch)
    {
        var fileSystem = new MockFileSystem();
        _ = fileSystem.Directory.CreateDirectory("spec");
        await fileSystem.File.WriteAllTextAsync(SnapshotPaths.SourceWatch, RefreshScenarioData.Serialize(watch));
        return new SourceWatchLoader(fileSystem);
    }
}
