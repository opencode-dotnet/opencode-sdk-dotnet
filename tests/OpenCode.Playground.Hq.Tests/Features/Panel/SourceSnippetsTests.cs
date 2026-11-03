using OpenCode.Playground.Hq.Features.Panel;

namespace OpenCode.Playground.Hq.Tests.Features.Panel;

public sealed class SourceSnippetsTests
{
    private const string BoardFile = "Features/Sessions/SessionBoard.cs";
    private readonly SourceSnippets _source = new();
    public static IEnumerable<PanelEntry> Entries() => [.. PanelEntry.FirstScreen, .. PanelEntry.SessionScreen];

    [Test]
    [MethodDataSource(nameof(Entries))]
    public async Task ReadAsync_Should_Resolve_Every_Panel_Entry_Without_Markers_Or_Common_Indentation(PanelEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var code = await _source.ReadAsync(entry.File, entry.Region);

        await Assert.That(code).IsNotEmpty();
        await Assert.That(code).DoesNotContain("#region");
        await Assert.That(code).DoesNotContain("#endregion");
        await Assert.That(code.Split('\n')[0]).IsEqualTo(code.Split('\n')[0].TrimStart());
    }

    [Test]
    public async Task ReadAsync_Should_Name_A_Missing_Region_And_Its_File()
    {
        var failure = await Assert.That(async () => await _source.ReadAsync(BoardFile, "missing")).Throws<InvalidOperationException>();

        await Assert.That(failure?.Message).IsEqualTo($"{BoardFile} has no complete region 'panel: missing'.");
    }

    [Test]
    public async Task ReadAsync_Should_Name_A_Missing_File()
    {
        var failure = await Assert.That(async () => await _source.ReadAsync("missing.cs", "reply")).Throws<InvalidOperationException>();

        await Assert.That(failure?.Message).IsEqualTo("missing.cs is not embedded.");
    }

    [Test]
    public async Task ReadAsync_Should_Return_The_Exact_Form_Reply_Statement_Unindented()
    {
        var code = await _source.ReadAsync(BoardFile, "form-reply");

        await Assert.That(code).IsEqualTo("_ = await client.Sessions.GetSessionClient(form.SessionId).ReplyToFormAsync(form.Id, reply, cancellationToken: cancellationToken);");
    }

    [Test]
    public async Task ReadAsync_Should_Keep_Relative_Indentation_In_A_Multiline_Region()
    {
        var code = await _source.ReadAsync(BoardFile, "reply");

        await Assert.That(code).IsEqualTo("_ = await client.Sessions.GetSessionClient(item.SessionId).ReplyToPermissionAsync(\n    item.Id, new SessionPermissionReplyRequest { Decision = decision }, cancellationToken: cancellationToken);");
    }
}
