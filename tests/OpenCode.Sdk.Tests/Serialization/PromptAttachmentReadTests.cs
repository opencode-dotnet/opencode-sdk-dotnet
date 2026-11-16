using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class PromptAttachmentReadTests
{
    [Test]
    public async Task SessionMessageUser_Should_Read_Inline_Media_And_Unknown_Files()
    {
        var payload = CreatePayload(message: true);

        var user = JsonSerializer.Deserialize(payload.ToJsonString(), OpenCodeJsonContext.Default.SessionMessageUser);

        await AssertFilesAsync(user?.Files ?? throw new InvalidOperationException("The message files were absent."));
    }

    [Test]
    public async Task SessionInboxUserPayload_Should_Read_Inline_Media_And_Unknown_Files()
    {
        var payload = CreatePayload(message: false);

        var user = JsonSerializer.Deserialize(payload.ToJsonString(), OpenCodeJsonContext.Default.SessionInboxUserPayload);

        await AssertFilesAsync(user?.Files ?? throw new InvalidOperationException("The inbox files were absent."));
    }

    private static JsonObject CreatePayload(bool message)
    {
        var corpus = JsonNode.Parse(new FixtureLoader().LoadJson("Unions.first-match-corpus.json"))!.AsArray();
        var attachments = corpus.Where(row => row!["union"]!.GetValue<string>() == "attachment").ToArray();
        var payload = new JsonObject
        {
            ["text"] = "attachments",
            ["files"] = new JsonArray(
                attachments[0]!["value"]!.DeepClone(),
                attachments[1]!["value"]!.DeepClone(),
                attachments[5]!["value"]!.DeepClone()),
        };
        if (message)
        {
            payload["id"] = "message";
            payload["time"] = new JsonObject { ["created"] = 1 };
            payload["type"] = "user";
        }

        return payload;
    }

    private static async Task AssertFilesAsync(IReadOnlyList<PromptAttachment> files)
    {
        await Assert.That(files.Count).IsEqualTo(3);
        await Assert.That(files[0].Kind).IsEqualTo(PromptAttachmentKind.PromptFileAttachment);
        await Assert.That(files[1].Kind).IsEqualTo(PromptAttachmentKind.PromptMediaFileAttachment);
        await Assert.That(files[2].Kind).IsEqualTo(PromptAttachmentKind.Unknown);
        var inline = files[0].PromptFileAttachment;
        await Assert.That(inline.Data).IsEqualTo("aGVsbG8=");
        await Assert.That(inline.Mime).IsEqualTo("text/plain");
        await Assert.That(inline.Source).IsTypeOf<PromptFileSourceInline>();
        var media = files[1].PromptMediaFileAttachment;
        await Assert.That(media.Uri).IsEqualTo("media:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        await Assert.That(media.Bytes).IsEqualTo(42);
        await Assert.That(media.Mime).IsEqualTo("image/png");
        await Assert.That(media.Source).IsTypeOf<PromptFileSourceInline>();
        await Assert.That(files[2].Unknown.GetProperty("mime").GetString()).IsEqualTo("image/png");
        await Assert.That(files[2].Unknown.GetProperty("source").GetProperty("type").GetString()).IsEqualTo("inline");
    }
}
