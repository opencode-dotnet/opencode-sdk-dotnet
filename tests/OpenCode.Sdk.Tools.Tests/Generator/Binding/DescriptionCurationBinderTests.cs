using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Serialization;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class DescriptionCurationBinderTests(PinnedBinding pinned)
{
    [Test]
    public async Task Apply_Should_Emit_A_Direct_Row_Without_Resolving_Conflicting_Owners()
    {
        var plan = EmitterPlanFixture.CreateHoistPlan();
        var carrier = plan.HoistedInterfaces.Single();
        var model = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "CreatedEventDurable");
        plan = plan with
        {
            HoistedInterfaces = [carrier, carrier with { Name = "IAOwner" }],
            Models = [model with { ImplementedHoistedInterfaceNames = [carrier.Name, "IAOwner"] }],
        };
        var errors = new BindingErrorCollector();

        var bound = DescriptionCurationBinder.Apply(plan,
        [
            Description("IDurableEnvelope", "Seq", "The first owner summary."),
            Description("IAOwner", "Seq", "The other owner summary."),
            Description("CreatedEventDurable", "Seq", "The direct summary.", "The direct remarks."),
        ], errors);

        await Assert.That(errors.Snapshot()).IsEquivalentTo(Array.Empty<BindingError>(), CollectionOrdering.Matching);
        var source = Unindented(Source(bound, "CreatedEventDurable"));
        await Assert.That(source).Contains("/// <summary>\n/// The direct summary.\n/// </summary>\n/// <remarks>\n/// The direct remarks.\n/// </remarks>");
        await Assert.That(source).DoesNotContain("<inheritdoc");
    }

    [Test]
    [Arguments("summary")]
    [Arguments("remarks")]
    [Arguments("identical")]
    public async Task Apply_Should_Refuse_Conflicting_Owners_Or_Select_The_Ordinal_First_Cref(string difference)
    {
        var plan = EmitterPlanFixture.CreateHoistPlan();
        var carrier = plan.HoistedInterfaces.Single();
        plan = plan with
        {
            HoistedInterfaces = [carrier, carrier with { Name = "IAOwner" }],
            Models = [.. plan.Models.Select(model => model is ObjectModelPlan concrete && concrete.ImplementedHoistedInterfaceNames.Count > 0
                ? concrete with { ImplementedHoistedInterfaceNames = [carrier.Name, "IAOwner"] } : model)],
        };
        var errors = new BindingErrorCollector();
        var rows = new[]
        {
            Description("IDurableEnvelope", "Seq", "The owner summary.", "The owner remarks."),
            Description("IAOwner", "Seq", difference == "summary" ? "Another summary." : "The owner summary.",
                difference == "remarks" ? "Other remarks." : "The owner remarks."),
        };

        var bound = DescriptionCurationBinder.Apply(plan, rows, errors);

        string[] subjects =
        [
            "CreatedEventDurable.Seq", "DeletedEventDurable.Seq", "PhaseEndedEventDurable.Seq", "PhaseStartedEventDurable.Seq",
        ];
        BindingError[] expected = difference == "identical" ? [] : [.. subjects.Select(static subject => new BindingError(BindingErrorCategory.Curation, subject,
            "description has conflicting curated owners: IAOwner.Seq, IDurableEnvelope.Seq"))];
        await Assert.That(errors.Snapshot()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        if (difference == "identical")
        {
            await Assert.That(Source(bound, "CreatedEventDurable")).Contains("/// <inheritdoc cref=\"IAOwner.Seq\" />");
        }
    }

    [Test]
    [Arguments("location", false, "must declare exactly one of location or canon")]
    [Arguments("excerpt", false, "excerpt must be non-empty")]
    [Arguments("location", true, "must declare exactly one of location or canon")]
    [Arguments("excerpt", true, "excerpt must be non-empty")]
    public async Task Apply_Should_Name_Missing_And_Null_Source_Fields(string field, bool explicitNull, string problem)
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        // The typed base serializes a non-null document with one row and one source.
        var json = JsonSerializer.SerializeToNode(EventCuration(Description("Notice", "Data")), ToolJsonContext.Default.GenerationCuration)!;
        var source = json["descriptions"]![0]!["sources"]![0]!.AsObject();
        if (explicitNull)
        {
            source[field] = null;
        }
        else
        {
            _ = source.Remove(field);
        }

        var fileSystem = new MockFileSystem();
        await fileSystem.File.WriteAllTextAsync("curation.json", json.ToJsonString(), CancellationToken.None);
        var exception = await Assert.That(async () =>
        {
            var curation = await new CurationLoader(fileSystem).LoadAsync("curation.json", CancellationToken.None);
            _ = new BindingTestHost().Bind(document, Selection("event.get"), curation);
        }).Throws<BindingException>();

        await Assert.That(exception!.Errors).IsEquivalentTo(
            [new BindingError(BindingErrorCategory.Curation, "Notice.Data", "description sources[0] " + problem)], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("missing", "sources[0] must declare a sources list")]
    [Arguments("empty-list", "sources[0] must declare 1 to 4 entries")]
    [Arguments("null-entry", "sources[0] must declare a source entry")]
    [Arguments("five", "sources[4] exceeds the maximum of 4 entries")]
    [Arguments("duplicate", "sources[1] repeats location 'packages/example.ts:1'")]
    [Arguments("location", "sources[0] location must be a packages/-relative path with a positive line")]
    [Arguments("empty", "sources[0] excerpt must be non-empty")]
    [Arguments("multiline", "sources[0] excerpt must be one line")]
    [Arguments("short", "sources[0] excerpt must contain 12 to 200 characters")]
    [Arguments("long", "sources[0] excerpt must contain 12 to 200 characters")]
    public async Task Apply_Should_Refuse_Invalid_Sources(string fault, string problem)
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), EventCuration());
        var source = new DescriptionSource { Location = "packages/example.ts:1", Excerpt = "An explicit declaration." };
        DescriptionSource? missingSource = null;
        IReadOnlyList<DescriptionSource?>? sources = fault switch
        {
            "missing" => null,
            "empty-list" => [],
            "null-entry" => [missingSource],
            "five" => [.. Enumerable.Range(1, 5).Select(index => source with { Location = "packages/example.ts:" + index.ToString(CultureInfo.InvariantCulture) })],
            "duplicate" => [source, source],
            "location" => [source with { Location = "packages/example.ts:0" }],
            "empty" => [source with { Excerpt = "" }],
            "multiline" => [source with { Excerpt = "A declaration\ncontinued." }],
            "short" => [source with { Excerpt = new string('x', 11) }],
            _ => [source with { Excerpt = new string('x', 201) }],
        };
        var errors = new BindingErrorCollector();

        await Assert.That(() => DescriptionCurationBinder.Apply(plan, [Description("Notice", "Data") with { Sources = sources }], errors)).ThrowsNothing();

        await Assert.That(errors.Snapshot()).IsEquivalentTo(
            [new BindingError(BindingErrorCategory.Curation, "Notice.Data", "description " + problem)], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(null, "curation is null")]
    [Arguments("Type", "curation names no type")]
    public async Task Bind_Should_Refuse_Unsafe_Description_Rows(string? blankField, string problem)
    {
        var exception = await BindUnsafeContractRowAsync("Descriptions", blankField);

        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation,
            "descriptions[0]", "descriptions[0] " + problem));
    }

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
    [Arguments("missing-type", "Missing", "description type does not exist in the generated plan")]
    [Arguments("unselected-type", "Unselected", "description type does not exist in the generated plan")]
    [Arguments("missing-member", "Notice.Absent", "description member does not exist as a declared property or operation method on the generated type")]
    [Arguments("wire-member", "Notice.data", "description member does not exist as a declared property or operation method on the generated type")]
    [Arguments("blank-summary", "Notice", "description curation must declare a non-empty summary")]
    [Arguments("blank-reason", "Notice", "description curation must declare a reason")]
    [Arguments("duplicate-type", "Notice", "description curation is duplicated")]
    [Arguments("duplicate-member", "Notice.Data", "description curation is duplicated")]
    public async Task Bind_Should_Refuse_Invalid_Description_Rows(string fault, string subject, string problem)
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

        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation, subject, problem));
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

        var source = Encoding.UTF8.GetString(UnionEmitter.Emit(plan.Unions, plan.Placement, plan.Namespaces)
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
        var curation = Curation(Groups("widget", RootGroup()), stringQueryParameters: [StringQueryParameter("widget.get", "query")]) with
        {
            Descriptions = [Description(type, member, remarks: "The curated remarks.")],
        };
        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), curation);

        var source = Encoding.UTF8.GetString(SourceEmitter.Emit(plan)
            .Single(source => source.RelativePath.EndsWith(type + ".cs", StringComparison.Ordinal)).Utf8Source.Span);

        await Assert.That(source).Contains("The curated summary.");
        await Assert.That(Unindented(source)).Contains("/// <remarks>\n/// The curated remarks.\n/// </remarks>");
    }

    /// <summary>A curated summary on a hoisted interface replaces the placeholder and keeps the consume-only remark.</summary>
    [Test]
    public async Task Emit_Should_Keep_The_Consume_Only_Remark_On_A_Curated_Hoisted_Interface()
    {
        var document = pinned.Document;
        var selection = pinned.Selection;
        var curation = pinned.Curation;
        var plan = new BindingTestHost().Bind(document, selection,
            curation with { Descriptions = [.. curation.Descriptions, Description("IDurableEnvelope", null, "A curated envelope summary.")] });

        var hoisted = plan.HoistedInterfaces.Single(static value => value.Name == "IDurableEnvelope");
        var source = Encoding.UTF8.GetString(HoistedInterfaceEmitter.Emit([hoisted], plan.Placement).Single().Utf8Source.Span);

        await Assert.That(source).Contains("A curated envelope summary.");
        await Assert.That(source).Contains("Consume this interface; do not implement it outside tests.");
    }

    private static string Source(EmitPlan plan, string type) => Encoding.UTF8.GetString(ModelEmitter.Emit(plan)
        .Single(source => source.RelativePath.EndsWith("/" + type + ".cs", StringComparison.Ordinal)).Utf8Source.Span);
}
