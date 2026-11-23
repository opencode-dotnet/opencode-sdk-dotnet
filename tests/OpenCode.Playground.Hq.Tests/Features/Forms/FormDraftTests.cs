using OpenCode.Playground.Hq.Features.Forms;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Playground.Hq.Tests.Features.Forms;

public sealed class FormDraftTests
{
    [Test]
    public async Task ToReply_Should_Send_The_Chosen_Option_And_Values_In_Option_Order()
    {
        var draft = new FormDraft(Form(
            new FormStringField { Key = "archive", Options = [Option("yes"), Option("no")] },
            new FormMultiselectField { Key = "formats", Options = [Option("csv"), Option("pdf"), Option("xml")] }));
        draft.SetText("archive", "no");
        draft.Toggle("formats", "xml");
        draft.Toggle("formats", "csv");
        draft.Toggle("formats", "pdf");
        draft.Toggle("formats", "pdf");
        var reply = draft.ToReply();

        await Assert.That(reply.Answer["archive"]).IsEqualTo(FormValue.FromText("no"));
        await Assert.That(reply.Answer["formats"].TextList).IsEquivalentTo(["csv", "xml"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ToReply_Should_Send_A_Custom_Answer_As_Typed()
    {
        var draft = new FormDraft(Form(new FormStringField { Key = "name", Options = [Option("preset")], Custom = true }));
        draft.SetText("name", "  My own answer\n");

        await Assert.That(draft.ToReply().Answer["name"]).IsEqualTo(FormValue.FromText("  My own answer\n"));
    }

    [Test]
    public async Task Answerable_Should_Refuse_An_Unsupported_Field()
    {
        var draft = new FormDraft(Form(new FormStringField { Key = "name" }, new FormNumberField { Key = "count" }));

        await Assert.That(draft.Answerable).IsFalse();
    }

    [Test]
    public async Task ShowsTextBox_Should_Hide_It_For_Options_Without_Custom()
    {
        await Assert.That(FormDraft.ShowsTextBox(new FormStringField { Key = "name", Options = [Option("preset")] })).IsFalse();
    }

    [Test]
    public async Task ToReply_Should_Refuse_An_Unsupported_Field()
    {
        var draft = new FormDraft(Form(new FormNumberField { Key = "count" }));

        await Assert.That(draft.ToReply).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Answerable_Should_Accept_Text_And_Multiselect_Fields()
    {
        var draft = new FormDraft(Form(new FormStringField { Key = "name" }, new FormMultiselectField { Key = "count", Options = [Option("1")] }));

        await Assert.That(draft.Answerable).IsTrue();
    }

    [Test]
    [Arguments(null, false)]
    [Arguments(0, false)]
    [Arguments(1, true)]
    public async Task ShowsTextBox_Should_Show_It_Without_Options_Or_With_Custom(int? count, bool custom)
    {
        var field = new FormStringField { Key = "name", Options = count is null ? null : [.. Enumerable.Range(0, count.Value).Select(static _ => Option("preset"))], Custom = custom };

        await Assert.That(FormDraft.ShowsTextBox(field)).IsTrue();
    }

    [Test]
    public async Task IsText_Should_Mark_Only_The_Selected_Option()
    {
        var draft = new FormDraft(Form(new FormStringField { Key = "name", Options = [Option("preset")] }));
        draft.SetText("name", "preset");

        await Assert.That(draft.IsText("name", "preset")).IsTrue();
        await Assert.That(draft.IsText("name", "other")).IsFalse();
        draft.SetText("name", "other");
        await Assert.That(draft.IsText("name", "preset")).IsFalse();
        await Assert.That(draft.IsText("name", "other")).IsTrue();
    }

    [Test]
    public async Task CustomTextOf_Should_Clear_The_Text_Box_When_An_Option_Is_Chosen()
    {
        var field = new FormStringField { Key = "name", Options = [Option("preset")], Custom = true };
        var draft = new FormDraft(Form(field));
        draft.SetText("name", "my name");
        await Assert.That(draft.CustomTextOf(field)).IsEqualTo("my name");
        draft.SetText("name", "preset");
        await Assert.That(draft.CustomTextOf(field)).IsEmpty();
    }

    private static FormOption Option(string value) => new() { Label = value, Value = value };
    private static FormInfo Form(params IFormField[] fields) => new() { Id = "frm_1", SessionId = "ses_1", Title = "Export", Fields = fields };
}
