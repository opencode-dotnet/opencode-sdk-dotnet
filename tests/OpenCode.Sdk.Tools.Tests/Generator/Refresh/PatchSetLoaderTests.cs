using System.Text;
using System.Text.Json.Nodes;
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
        await Assert.That(patch.PatchPath).Satisfies(fileSystem.File.Exists);
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

    [Test]
    public async Task LoadAsync_Should_Refuse_A_Component_Predicate_Without_A_Keyword()
    {
        var manifest = RefreshScenarioData.Manifest(sha256: PatchSha());
        // The typed builder emits a non-null manifest with a repairPredicate object.
        var json = JsonNode.Parse(RefreshScenarioData.Serialize(manifest))!;
        _ = json["repairPredicate"]!.AsObject().Remove("keyword");
        var loader = new PatchSetLoader(await CreatePatchSetAsync(manifest, json.ToJsonString()));

        var exception = await Assert.That(async () => _ = await loader.LoadAsync(CancellationToken.None))
            .Throws<SnapshotRefreshException>();

        await Assert.That(exception!.Message).IsEqualTo("patch manifest '001-test.json' declares an incomplete repair predicate");
    }

    [Test]
    [Arguments("valid")]
    [Arguments("empty operations")]
    [Arguments("blank operation")]
    [Arguments("blank parameter")]
    [Arguments("blank style")]
    public async Task LoadAsync_Should_Validate_A_Parameter_Style_Predicate(string variation)
    {
        var manifest = RefreshScenarioData.Manifest(sha256: PatchSha());
        // The typed builder emits a non-null manifest with a repairPredicate object.
        var json = JsonNode.Parse(RefreshScenarioData.Serialize(manifest))!;
        var predicate = json["repairPredicate"]!.AsObject();
        predicate.Clear();
        predicate["type"] = "parameterLacksStyle";
        predicate["operations"] = variation switch
        {
            "empty operations" => [],
            "blank operation" => new JsonArray("experimental.artifacts.list", " "),
            _ => new JsonArray("experimental.artifacts.list"),
        };
        predicate["parameter"] = variation == "blank parameter" ? " " : "location";
        predicate["style"] = variation == "blank style" ? " " : "deepObject";
        var loader = new PatchSetLoader(await CreatePatchSetAsync(manifest, json.ToJsonString()));

        if (variation == "valid")
        {
            IReadOnlyList<LoadedPatch>? patches = null;
            await Assert.That(async () => patches = await loader.LoadAsync(CancellationToken.None)).ThrowsNothing();
            // A successful LoadAsync returns its non-null patch list.
            await Assert.That(patches!.Single().Manifest.RepairPredicate.Type).IsEqualTo("parameterLacksStyle");
        }
        else
        {
            var exception = await Assert.That(async () => _ = await loader.LoadAsync(CancellationToken.None))
                .Throws<SnapshotRefreshException>();
            // Throws has established the expected exception instance.
            await Assert.That(exception!.Message).IsEqualTo("patch manifest '001-test.json' declares an incomplete repair predicate");
        }
    }

    private static async Task<MockFileSystem> CreatePatchSetAsync(PatchManifest manifest, string? json = null)
    {
        var fileSystem = new MockFileSystem();
        _ = fileSystem.Directory.CreateDirectory("spec/patches");
        await fileSystem.File.WriteAllTextAsync("spec/patches/001-test.json", json ?? RefreshScenarioData.Serialize(manifest));
        await fileSystem.File.WriteAllTextAsync("spec/patches/001-test.patch", PatchContent);
        return fileSystem;
    }

    private static string PatchSha() => DocumentInspector.Sha256Hex(Encoding.UTF8.GetBytes(PatchContent));
}
