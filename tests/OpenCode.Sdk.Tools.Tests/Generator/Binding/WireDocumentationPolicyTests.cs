using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class WireDocumentationPolicyTests(PinnedBinding pinned)
{
    [Test]
    public async Task Bind_Should_Document_An_Event_By_Its_Literal_Type()
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), Curation(Groups("event", RootGroup())));

        await Assert.That(plan.Models.Single(static model => model.Name == "Notice").Documentation.Summary)
            .IsEqualTo("The 'notice.changed' event.");
        var source = Encoding.UTF8.GetString(ModelEmitter.Emit(plan).Single(static source => source.RelativePath.EndsWith("/Notice.cs", StringComparison.Ordinal)).Utf8Source.Span);
        await Assert.That(source).Contains("The &apos;notice.changed&apos; event.");
    }

    [Test]
    public async Task Bind_Should_Document_An_Exclusive_Event_Payload()
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), Curation(Groups("event", RootGroup())));

        await Assert.That(plan.Models.Single(static model => model.Name == "Details").Documentation.Summary)
            .IsEqualTo("The data of the 'notice.changed' event.");
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Bind_Should_Keep_Todays_Text_For_A_Payload_Used_Twice(bool sharedPayload, bool payloadElsewhere)
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario(sharedPayload, payloadElsewhere));
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), Curation(Groups("event", RootGroup())));

        await Assert.That(plan.Models.Single(static model => model.Name == "Details").Documentation.Summary).IsNull();
        var source = Encoding.UTF8.GetString(ModelEmitter.Emit(plan).Single(static source => source.RelativePath.EndsWith("/Details.cs", StringComparison.Ordinal)).Utf8Source.Span);
        await Assert.That(source).Contains("Represents a details value.");
    }

    [Test]
    public async Task Bind_Should_Let_A_Spec_Description_Win_Over_A_Wire_Role()
    {
        const string description = "A schema-described notification.";
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario(description: description));
        var plan = new BindingTestHost().Bind(document, Selection("event.get"), Curation(Groups("event", RootGroup())));

        var source = Encoding.UTF8.GetString(ModelEmitter.Emit(plan).Single(static source => source.RelativePath.EndsWith("/Notice.cs", StringComparison.Ordinal)).Utf8Source.Span);
        await Assert.That(source).Contains(description);
        await Assert.That(source).DoesNotContain("The &apos;notice.changed&apos; event.");
    }

    [Test]
    public async Task Bind_Should_Document_A_Single_Operation_Body()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("widget.create", "post", "/api/widgets", static operation => operation
                .RequestBody("application/json", static schema => schema.Type("object")
                    .Property("text", static property => property.Type("string")), required: true)
                .WithoutResponse(200).Response(204))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.create"), Curation(Groups("widget", RootGroup())));

        await Assert.That(plan.Models.Single(static model => model.Name == "WidgetCreateRequest").Documentation.Summary)
            .IsEqualTo("The body of 'widget.create' (POST /api/widgets).");
    }

    [Test]
    public async Task Bind_Should_Document_A_Response_Envelope()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("widget.get", path: "/api/widgets", configure: static operation => operation.WithoutResponse(200).Response(204))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), Curation(Groups("widget", RootGroup())));

        await Assert.That(plan.Clients.SelectMany(static client => client.Operations).Single().Envelope!.Documentation.Summary)
            .IsEqualTo("The response of 'widget.get' (GET /api/widgets).");
        var source = Encoding.UTF8.GetString(EnvelopeEmitter.Emit(plan.Clients).Single().Utf8Source.Span);
        await Assert.That(source).Contains("The response of &apos;widget.get&apos; (GET /api/widgets).");
    }

    [Test]
    public async Task Bind_Should_Document_A_Query_Request()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("widget.get", path: "/api/widgets", configure: static operation => operation
                .Parameter("query", "query", static schema => schema.Type("string"))
                .WithoutResponse(200).Response(204))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), Curation(Groups("widget", RootGroup()),
            stringQueryParameters: [StringQueryParameter("widget.get", "query")]));

        await Assert.That(plan.Clients.SelectMany(static client => client.Operations).Single().QueryRequest!.Documentation.Summary)
            .IsEqualTo("The query of 'widget.get' (GET /api/widgets).");
        var source = Encoding.UTF8.GetString(QueryRequestEmitter.Emit(plan.Clients).Single().Utf8Source.Span);
        await Assert.That(source).Contains("The query of &apos;widget.get&apos; (GET /api/widgets).");
    }

    [Test]
    public async Task Bind_Should_Document_An_Error_By_Its_Tag()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("Rejected", static schema => schema.Type("object")
                .Property("_tag", static property => property.Type("string").Const("bad.input"), required: true)
                .Property("message", static property => property.Type("string"), required: true))
            .WithOperation("widget.get", configure: static operation => operation.WithoutResponse(200).Response(204)
                .Response(400, "application/json", static schema => schema.Ref("Rejected")))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), Curation(Groups("widget", RootGroup())));

        await Assert.That(plan.Models.Single(static model => model.Name == "Rejected").Documentation.Summary)
            .IsEqualTo("The 'bad.input' error.");
    }

    [Test]
    public async Task Bind_Should_Keep_Todays_Text_When_A_Record_Has_Two_Wire_Roles()
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario(carriesBody: true));
        var plan = new BindingTestHost().Bind(document, Selection("event.get", "event.create"), Curation(Groups("event", RootGroup())));

        // The body names the shared record, so the event arm and the request are one type.
        var record = plan.Models.Single(static model => model.Name == "EventCreateRequest");
        await Assert.That(record.Documentation.Summary).IsNull();
        var source = Encoding.UTF8.GetString(ModelEmitter.Emit(plan).Single(static source => source.RelativePath.EndsWith("/EventCreateRequest.cs", StringComparison.Ordinal)).Utf8Source.Span);
        await Assert.That(source).Contains("Represents an event create request value.");
    }

    [Test]
    public async Task Bind_Should_Not_Call_A_Non_Event_Type_Discriminated_Arm_An_Event()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("TextPart", static schema => schema.Type("object")
                .Property("type", static property => property.Type("string").Const("text"), required: true))
            .WithSchema("FilePart", static schema => schema.Type("object")
                .Property("type", static property => property.Type("string").Const("file"), required: true))
            .WithSchema("Parts", static schema => schema.AnyOf(static arm => arm.Ref("TextPart"), static arm => arm.Ref("FilePart")))
            .WithOperation("part.get", configure: static operation => operation.Response(200, "application/json", static schema => schema.Ref("Parts")))));
        var plan = new BindingTestHost().Bind(document, Selection("part.get"), Curation(Groups("part", RootGroup())));

        await Assert.That(plan.Models.Single(static model => model.Name == "TextPart").Documentation.Summary).IsNull();
    }

    [Test]
    public async Task Bind_Should_Keep_Todays_Text_For_A_Merged_Body_And_Query_Record()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("widget.create", "post", configure: static operation => operation
                .RequestBody("application/json", static schema => schema.Type("object")
                    .Property("text", static property => property.Type("string")), required: true)
                .Parameter("location", "query", NullableLocationSelector, deepObject: true)
                .WithoutResponse(200).Response(204))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.create"), Curation(Groups("widget", RootGroup())));

        await Assert.That(plan.Models.Single(static model => model.Name == "WidgetCreateRequest").Documentation.Summary).IsNull();
    }

    [Test]
    public async Task Bind_Should_Let_An_Envelope_Schema_Description_Win()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation("widget.get", configure: static operation => operation.Response(200, "application/json", static schema => schema
                .Type("object").Description("The described response wrapper.")
                .Property("data", static property => property.Type("object")
                    .Property("text", static member => member.Type("string"), required: true), required: true)))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), Curation(Groups("widget", RootGroup())));

        var source = Encoding.UTF8.GetString(EnvelopeEmitter.Emit(plan.Clients).Single().Utf8Source.Span);

        await Assert.That(source).Contains("The described response wrapper.");
        await Assert.That(source).DoesNotContain("The response of &apos;widget.get&apos;");
    }

    /// <summary>A payload that is also an operation's body has two roles, so it keeps today's text rather than either summary.</summary>
    [Test]
    public async Task Bind_Should_Keep_Todays_Text_When_A_Payload_Is_Also_A_Body()
    {
        var document = await BindingTestHost.IngestAsync(new EventDocumentationScenario(payloadAsBody: true));
        var plan = new BindingTestHost().Bind(document, Selection("event.get", "event.create"), Curation(Groups("event", RootGroup())));

        var notice = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Notice");
        var payload = (NamedTypeReferencePlan)notice.Properties.Single(static property => property.WireName == "data").Type;
        await Assert.That(plan.Models.Single(model => model.Name == payload.Name).Documentation.Summary).IsNull();
    }

    /// <summary>Stream failure causes share the errors' marker but are not API errors, so they are not called errors.</summary>
    [Test]
    public async Task Bind_Should_Call_Only_The_Error_Spine_Records_Errors()
    {
        var plan = pinned.Plan;

        await Assert.That(plan.Models.Single(static model => model.Name == "SessionNotFoundError").Documentation.Summary)
            .IsEqualTo("The 'SessionNotFoundError' error.");
        await Assert.That(plan.Models.Single(static model => model.Name == "StreamFailureCauseInterrupt").Documentation.Summary).IsNull();
    }

    /// <summary>The location selector query parameter every location-scoped operation declares: an object with a nullable directory, or null.</summary>
    private static void NullableLocationSelector(SchemaBuilder schema) => schema.AnyOf(
        static branch => branch
            .Type("object")
            .AdditionalPropertiesFalse()
            .Property("directory", static property => property.AnyOf(
                static inner => inner.Type("string"),
                static inner => inner.Type("null"))),
        static branch => branch.Type("null"));
}
