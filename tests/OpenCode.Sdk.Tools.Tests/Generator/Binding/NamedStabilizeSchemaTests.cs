using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class NamedStabilizeSchemaTests
{
    [Test]
    public async Task Bind_Should_Preserve_Different_Numbered_Shapes_With_Explicit_Names()
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("widget", RootGroup()),
            schemaNames: [SchemaName("Widget_1", "ExtendedWidget")]);

        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), curation);

        var primary = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Widget");
        var secondary = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "ExtendedWidget");
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(primary.Properties.Select(static property => property.WireName)).IsEquivalentTo(["id"], CollectionOrdering.Any);
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(secondary.Properties.Select(static property => property.WireName)).IsEquivalentTo(["id", "extra"], CollectionOrdering.Any);
        await Assert.That(secondary.Properties.Single(static property => property.WireName == "extra").IsRequired).IsFalse();
        await Assert.That(plan.ImplicitAliases.Aliases).DoesNotContainKey("Widget_1");
        var holder = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Holder");
        await Assert.That(((NamedTypeReferencePlan)holder.Properties.Single(static property => property.WireName == "primary").Type).Name).IsEqualTo("Widget");
        await Assert.That(((NamedTypeReferencePlan)holder.Properties.Single(static property => property.WireName == "secondary").Type).Name).IsEqualTo("ExtendedWidget");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Name_That_Cannot_Give_An_Array_A_Distinct_Model_Identity()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("Values", static schema => schema.Type("array").Items(static item => item.Type("string")))
            .WithSchema("Values_1", static schema => schema.Type("array").Items(static item => item.Type("integer")))
            .WithSchema("Holder", static schema => schema.Type("object")
                .Property("primary", static property => property.Ref("Values"), required: true)
                .Property("secondary", static property => property.Ref("Values_1"), required: true))
            .WithOperation("widget.get", path: "/api/widget", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Holder")))));
        var curation = Curation(Groups("widget", RootGroup()),
            schemaNames: [SchemaName("Values_1", "NumericValues")]);

        var exception = RefuseWidget(document, curation);

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Values_1"
            && error.Problem.Contains("not structurally identical", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Name_For_A_Different_Numbered_Envelope()
    {
        // A data-carrying wrapper is response spine the dialect never names, so a schema name
        // cannot give a numbered wrapper a distinct model identity: it must still equal its base.
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("Envelope", static schema => schema.Type("object")
                .Property("data", static property => property.Type("string"), required: true))
            .WithSchema("Envelope_1", static schema => schema.Type("object")
                .Property("data", static property => property.Type("integer"), required: true))
            .WithOperation("widget.get", path: "/api/widget", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Envelope")))
            .WithOperation("widget.list", path: "/api/widgets", configure: static operation => operation
                .Response(200, "application/json", static schema => schema.Ref("Envelope_1")))));
        var curation = Curation(Groups("widget", RootGroup()),
            schemaNames: [SchemaName("Envelope_1", "NumericEnvelope")]);

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(
            document, Selection("widget.get", "widget.list"), curation));

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Envelope_1"
            && error.Problem.Contains("not structurally identical", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("Widget", "Different shape", "collides")]
    [Arguments("ExtendedWidget", " ", "declare a reason")]
    [Arguments("invalid-name", "Different shape", "valid C# identifier")]
    public async Task Bind_Should_Refuse_Invalid_Distinct_Schema_Curation(string name, string reason, string problem)
    {
        var document = await BindingTestHost.IngestAsync(Scenario());
        var curation = Curation(Groups("widget", RootGroup()),
            schemaNames: [SchemaName("Widget_1", name, reason)]);

        var exception = RefuseWidget(document, curation);

        await Assert.That(exception.Errors).Contains(error => error.Subject == "Widget_1"
            && error.Problem.Contains(problem, StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Name_For_A_Numbered_Shape_That_Folds()
    {
        // The pair is equivalent, so the mechanical collapse folds it whatever the row says; a row
        // that no longer distinguishes anything is refused instead of kept, so the refresh that
        // made the shapes equal surfaces it.
        var document = await BindingTestHost.IngestAsync(Scenario(equivalent: true));
        var curation = Curation(Groups("widget", RootGroup()),
            schemaNames: [SchemaName("Widget_1", "ExtendedWidget")]);

        var exception = RefuseWidget(document, curation);

        await Assert.That(exception.Errors).Contains(static error => error.Subject == "Widget_1"
            && error.Problem.Contains("folds into", StringComparison.Ordinal)
            && error.Problem.Contains("remove the row", StringComparison.Ordinal));
    }

    [Test]
    public async Task Bind_Should_Fold_An_Equivalent_Numbered_Shape_Without_A_Name()
    {
        var document = await BindingTestHost.IngestAsync(Scenario(equivalent: true));
        var curation = Curation(Groups("widget", RootGroup()));

        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), curation);

        await Assert.That(plan.ImplicitAliases.Aliases["Widget_1"]).IsEqualTo("Widget");
        var holder = plan.Models.OfType<ObjectModelPlan>().Single(static model => model.Name == "Holder");
        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(holder.Properties.Select(static property => ((NamedTypeReferencePlan)property.Type).Name))
            .IsEquivalentTo(["Widget", "Widget"], CollectionOrdering.Any);
    }

    private static BindingException RefuseWidget(SpecDocument document, GenerationCuration curation) =>
        Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("widget.get"), curation));

    private static SpecScenario Scenario(bool equivalent = false) => SpecScenario.Define(spec => spec
        .WithSchema("Widget", static schema => schema.Type("object")
            .Property("id", static property => property.Type("string"), required: true))
        .WithSchema("Widget_1", schema =>
        {
            _ = schema.Type("object").Property("id", static property => property.Type("string"), required: true);
            if (!equivalent)
            {
                _ = schema.Property("extra", static property => property.Type("string"));
            }
        })
        .WithSchema("Holder", static schema => schema.Type("object")
            .Property("primary", static property => property.Ref("Widget"), required: true)
            .Property("secondary", static property => property.Ref("Widget_1"), required: true))
        .WithOperation("widget.get", path: "/api/widget", configure: static operation => operation
            .Response(200, "application/json", static schema => schema.Ref("Holder"))));
}
