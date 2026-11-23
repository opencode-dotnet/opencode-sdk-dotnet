using System.Text.Json;
using OpenCode.Playground.Hq.Features.Sessions;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Playground.Hq.Tests.Features.Sessions;

public sealed class NeedsYouItemTests
{
    [Test]
    public async Task PreviewFiles_Should_Keep_The_File_And_Patch_From_Metadata()
    {
        var files = JsonSerializer.SerializeToElement(new[] { new { file = "a.cs", patch = "@@ -1 +1 @@\n-old\n+new" } });
        var item = NeedsYouItem.FromPermission(new PermissionRequest
        {
            Id = "ask",
            SessionId = "session",
            Action = "edit",
            Resources = ["a.cs"],
            Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["files"] = files },
        });

        await Assert.That(item.PreviewFiles.Select(static file => file.Key)).IsEquivalentTo(["a.cs"], CollectionOrdering.Matching);
        await Assert.That(item.PreviewFiles.Single().Value).IsEqualTo("@@ -1 +1 @@\n-old\n+new");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PreviewFiles_Should_Omit_Missing_Patches_And_Missing_Files(bool missingPatch)
    {
        var item = new NeedsYouItem("ask", "session", "edit")
        {
            Metadata = missingPatch
                ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["files"] = JsonSerializer.SerializeToElement(new[] { new { file = "a.cs" } }),
                }
                : null,
        };

        await Assert.That(item.PreviewFiles).IsEmpty();
    }

    [Test]
    public async Task KindFor_Should_Use_The_Upstream_Description_Or_No_Description()
    {
        await Assert.That(NeedsYouItem.KindFor("edit")).IsEqualTo("Modify files, including edits, writes, and patches");
        await Assert.That(NeedsYouItem.KindFor("shell")).IsNull();
    }
}
