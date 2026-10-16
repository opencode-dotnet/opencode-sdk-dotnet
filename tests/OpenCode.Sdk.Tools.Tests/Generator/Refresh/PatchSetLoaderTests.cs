using System.Text;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Generator.Refresh;

public sealed class PatchSetLoaderTests
{
    private const string PatchContent = "diff --git a/x b/x";

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Null_Touches_Member()
    {
        var manifest = RefreshScenarioData.Manifest(sha256: PatchSha());
        var fileSystem = await CreatePatchSetAsync(manifest);
        await fileSystem.File.WriteAllTextAsync("spec/patches/001-test.json",
            RefreshScenarioData.WithNullMember(RefreshScenarioData.Serialize(manifest), "touches"));

        var exception = await Assert.That(async () => _ = await new PatchSetLoader(fileSystem)
            .LoadAsync(CancellationToken.None)).Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).StartsWith("patch manifest '001-test.json' is invalid:");
        await Assert.That(exception.Message).Contains("touches");
    }

    [Test]
    public async Task LoadAsync_Should_Return_Empty_Without_A_Patches_Directory()
    {
        var loader = new PatchSetLoader(new MockFileSystem());

        var patches = await loader.LoadAsync(CancellationToken.None);

        await Assert.That(patches).IsEmpty();
    }

    [Test]
    public async Task LoadAsync_Should_Load_A_Valid_Manifest_With_Its_Patch()
    {
        var fileSystem = await CreatePatchSetAsync(RefreshScenarioData.Manifest(sha256: PatchSha()));
        var loader = new PatchSetLoader(fileSystem);

        var patches = await loader.LoadAsync(CancellationToken.None);

        var patch = patches.Single();
        await Assert.That(patch.ManifestName).IsEqualTo("001-test.json");
        await Assert.That(patch.Manifest.Order).IsEqualTo(1);
        await Assert.That(fileSystem.File.Exists(patch.PatchPath)).IsTrue();
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Hash_Mismatch()
    {
        var loader = new PatchSetLoader(await CreatePatchSetAsync(RefreshScenarioData.Manifest(sha256: new string('0', 64))));

        var exception = await Assert.That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("does not match its manifest hash");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Missing_Patch_File()
    {
        var fileSystem = new MockFileSystem();
        _ = fileSystem.Directory.CreateDirectory("spec/patches");
        await fileSystem.File.WriteAllTextAsync(
            "spec/patches/001-test.json",
            RefreshScenarioData.Serialize(RefreshScenarioData.Manifest(sha256: PatchSha())));
        var loader = new PatchSetLoader(fileSystem);

        var exception = await Assert.That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("missing patch file");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_Duplicate_Order_Positions()
    {
        var fileSystem = await CreatePatchSetAsync(RefreshScenarioData.Manifest(sha256: PatchSha()));
        await fileSystem.File.WriteAllTextAsync(
            "spec/patches/002-test.json",
            RefreshScenarioData.Serialize(RefreshScenarioData.Manifest(sha256: PatchSha())));
        var loader = new PatchSetLoader(fileSystem);

        var exception = await Assert.That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("duplicate order");
    }

    [Test]
    public async Task LoadAsync_Should_Refuse_An_Unsupported_Predicate_Type()
    {
        var manifest = RefreshScenarioData.Manifest(sha256: PatchSha()) with
        {
            RepairPredicate = new PatchPredicate
            {
                Type = "mystery",
                Components = ["V2EventEncoded"],
                Keyword = "contentSchema",
            },
        };
        var loader = new PatchSetLoader(await CreatePatchSetAsync(manifest));

        var exception = await Assert.That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).Contains("unsupported predicate type 'mystery'");
    }

    private static async Task<MockFileSystem> CreatePatchSetAsync(PatchManifest manifest)
    {
        var fileSystem = new MockFileSystem();
        _ = fileSystem.Directory.CreateDirectory("spec/patches");
        await fileSystem.File.WriteAllTextAsync("spec/patches/001-test.json", RefreshScenarioData.Serialize(manifest));
        await fileSystem.File.WriteAllTextAsync("spec/patches/001-test.patch", PatchContent);
        return fileSystem;
    }

    private static string PatchSha() => DocumentInspector.Sha256Hex(Encoding.UTF8.GetBytes(PatchContent));
}
