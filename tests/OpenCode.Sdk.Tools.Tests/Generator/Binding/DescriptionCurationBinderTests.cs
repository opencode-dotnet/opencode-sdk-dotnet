using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class DescriptionCurationBinderTests
{
    [Test]
    public async Task Bind_Should_Replace_The_Summary_Even_When_The_Spec_Describes_The_Type()
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario(description: "The schema summary."));
        var curation = EventCuration(Description("Notice", summary: "The curated notification."));

        var plan = new BindingTestHost().Bind(document, Selection("event.get"), curation);

        await Assert.That(plan.Models.Single(static model => model.Name == "Notice").Documentation.Summary)
            .IsEqualTo("The curated notification.");
        var source = Source(plan, "Notice");
        await Assert.That(source).Contains("The curated notification.");
        await Assert.That(source).DoesNotContain("The schema summary.");
    }

    [Test]
    public async Task Bind_Should_Replace_A_Wire_Derived_Summary()
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), EventCuration(Description("Notice")));

        await Assert.That(Source(plan, "Notice")).Contains("The curated summary.");
        await Assert.That(Source(plan, "Notice")).DoesNotContain("The &apos;notice.changed&apos; event.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Emit_Should_Escape_Curated_Summaries_And_Normalize_Remarks(bool forMember)
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        var row = Description("Notice", forMember ? "Data" : null, "A <value> & \"quoted\" 'text'.", "Read <data> & keep it.\nNext line.");
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), EventCuration(row));

        var source = Source(plan, "Notice");

        await Assert.That(source).Contains("A &lt;value&gt; &amp; &quot;quoted&quot; &apos;text&apos;.");
        await Assert.That(Unindented(source)).Contains("/// <remarks>\n/// Read &lt;data&gt; &amp; keep it. Next line.\n/// </remarks>");
        await Assert.That(source).DoesNotContain("/// Read <data>");
    }

    [Test]
    [Arguments("missing-type", "type does not exist")]
    [Arguments("unselected-type", "type does not exist")]
    [Arguments("missing-member", "member does not exist")]
    [Arguments("wire-member", "member does not exist")]
    [Arguments("blank-summary", "non-empty summary")]
    [Arguments("blank-reason", "must declare a reason")]
    [Arguments("duplicate-type", "is duplicated")]
    [Arguments("duplicate-member", "is duplicated")]
    public async Task Bind_Should_Refuse_Invalid_Description_Rows(string fault, string problem)
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        var row = fault switch
        {
            "missing-type" => Description("Missing"),
            "unselected-type" => Description("Unselected"),
            "missing-member" => Description("Notice", "Absent"),
            "wire-member" => Description("Notice", "data"),
            "blank-summary" => Description("Notice", summary: " "),
            "blank-reason" => Description("Notice") with { Reason = " " },
            "duplicate-member" => Description("Notice", "Data"),
            _ => Description("Notice"),
        };
        var curation = fault.StartsWith("duplicate-", StringComparison.Ordinal)
            ? EventCuration(row, row with { Summary = "Another summary." })
            : EventCuration(row);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("event.get"), curation));

        await Assert.That(exception.Errors.Any(error => error.Category is BindingErrorCategory.Curation
            && error.Problem.Contains(problem, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Bind_Should_Address_An_Extensible_Enum_Value_By_Its_Curated_CSharp_Name()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("Mode", static schema => schema.Type("string").Enum("fast", "slow"))
            .WithSchema("Holder", static schema => schema.Type("object").Property("mode", static property => property.Ref("Mode"), required: true))
            .WithOperation("mode.get", configure: static operation => operation.Response(200, "application/json", static schema => schema.Ref("Holder")))));
        var curation = Curation(Groups("mode", RootGroup()), enumMemberNames: [EnumMemberName("Mode", "fast", "Quick")]) with
        {
            Descriptions = [Description("Mode", "Quick", "The fast wire value.", "A pinned value.")],
        };
        var plan = new BindingTestHost().Bind(document, Selection("mode.get"), curation);

        var source = Source(plan, "Mode");

        await Assert.That(source).Contains("The fast wire value.");
        await Assert.That(Unindented(source)).Contains("/// <remarks>\n/// A pinned value.\n/// </remarks>");
        await Assert.That(source).Contains("public static Mode Quick");
        await Assert.That(source).DoesNotContain("Gets the &apos;fast&apos; wire value.");
    }

    [Test]
    public async Task Emit_Should_Add_Curated_Interface_Remarks_Without_Losing_The_Pin_Bound_Contract()
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), EventCuration(
            Description("IEvents", summary: "The notification interface.", remarks: "Read the notification type."),
            Description("IEvents", "Type", "The notification's wire type.")));

        var source = Encoding.UTF8.GetString(UnionEmitter.Emit(plan.Unions, plan.Placement)
            .Single(static source => source.RelativePath.EndsWith("/IEvents.cs", StringComparison.Ordinal)).Utf8Source.Span);

        await Assert.That(source).Contains("The notification interface.");
        await Assert.That(source).Contains("The notification&apos;s wire type.");
        await Assert.That(source).Contains("Consume this interface; do not implement it outside tests.");
        await Assert.That(source).Contains("Read the notification type.");
    }

    [Test]
    [Arguments("WidgetRequest", "Query")]
    [Arguments("WidgetResponse", null)]
    [Arguments("OpenCodeClient", null)]
    public async Task Emit_Should_Apply_Rows_To_Operation_Records_And_Clients(string type, string? member)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("widget.get", configure: static operation => operation
                .Parameter("query", "query", static schema => schema.Type("string"))
                .WithoutResponse(200).Response(204))));
        var curation = Curation(Groups("widget", RootGroup())) with { Descriptions = [Description(type, member, remarks: "The curated remarks.")] };
        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), curation);

        var source = Encoding.UTF8.GetString(SourceEmitter.Emit(plan)
            .Single(source => source.RelativePath.EndsWith(type + ".cs", StringComparison.Ordinal)).Utf8Source.Span);

        await Assert.That(source).Contains("The curated summary.");
        await Assert.That(Unindented(source)).Contains("/// <remarks>\n/// The curated remarks.\n/// </remarks>");
    }

    private static GenerationCuration EventCuration(params DescriptionCuration[] rows) =>
        Curation(Groups("event", RootGroup())) with { Descriptions = rows };

    private static DescriptionCuration Description(string type, string? member = null, string summary = "The curated summary.", string? remarks = null) =>
        new() { Type = type, Member = member, Summary = summary, Remarks = remarks, Reason = "Describe the scenario's generated C# surface." };

    /// <summary>A member's documentation is indented with its member; the assertions read it unindented.</summary>
    private static string Unindented(string source) => string.Join('\n', source.Split('\n').Select(static line => line.TrimStart()));

    private static string Source(EmitPlan plan, string type) => Encoding.UTF8.GetString(ModelEmitter.Emit(plan)
        .Single(source => source.RelativePath.EndsWith("/" + type + ".cs", StringComparison.Ordinal)).Utf8Source.Span);
}
