using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Serialization;

public sealed class PromptAttachmentReadTests
{
    private readonly FixtureLoader _fixtures = new();
    private readonly GeneratedJsonSerializer _serializer = new();

    [Test]
    public async Task SessionMessageUser_Should_Read_Inline_Media_And_Unknown_Files()
    {
        var json = _fixtures.LoadJson("Serialization.session-message-user-attachments.json");

        var message = _serializer.Deserialize<ISessionMessageInfo>(json);

        await Assert.That(message).IsTypeOf<SessionMessageUser>();
        await AssertFilesAsync(((SessionMessageUser)message).Files);
    }

    [Test]
    public async Task SessionInboxUserPayload_Should_Read_Inline_Media_And_Unknown_Files()
    {
        var json = _fixtures.LoadJson("Serialization.session-inbox-user-payload-attachments.json");

        var payload = _serializer.Deserialize<SessionInboxUserPayload>(json);

        await AssertFilesAsync(payload.Files);
    }

    private static async Task AssertFilesAsync(IReadOnlyList<PromptAttachment>? files)
    {
        await Assert.That(files).IsNotNull();
        await Assert.That(files!.Select(static file => file.Kind)).IsEquivalentTo(
            [PromptAttachmentKind.PromptFileAttachment, PromptAttachmentKind.PromptMediaFileAttachment, PromptAttachmentKind.Unknown],
            CollectionOrdering.Matching);
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
