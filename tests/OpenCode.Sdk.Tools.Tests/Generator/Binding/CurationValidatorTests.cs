using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// Marked-union rows name interfaces with an upper-case concept, so removing the leading I
/// preserves the concept in unknown carriers and converters.
/// </summary>
public sealed class CurationValidatorTests
{
    private const string OperationId = "integration.get";
    private const string Union = "Integration.Event";

    [Test]
    [Arguments("IntegrationFoo")]
    public async Task ValidateCollapsedSchemas_Should_Refuse_A_Marked_Union_Row_That_Is_Not_An_Interface_Name(string name)
    {
        var exception = await BindRefusalsAsync(IntegrationScenario(), RowCuration(name), OperationId);

        await Assert.That(exception.Errors).IsEquivalentTo([new BindingError(BindingErrorCategory.Naming, Union,
            $"schema name '{name}' names a marked union, so it must be 'I' followed by an upper-case concept name")], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("IIntegrationFoo", "UnknownIntegrationFoo")]
    public async Task ValidateCollapsedSchemas_Should_Accept_A_Marked_Union_Row_That_Is_An_Interface_Name(string name, string unknownName)
    {
        var document = await BindingTestHost.IngestAsync(IntegrationScenario());

        var plan = new BindingTestHost().Bind(document, Selection(OperationId), RowCuration(name));

        await Assert.That(plan.Unions.Single(union => union.Name == name).UnknownTypeName).IsEqualTo(unknownName);
    }

    private static GenerationCuration RowCuration(string name) =>
        Curation(Groups("integration", RootGroup()), schemaNames: [SchemaName(Union, name)]);

    private static SpecScenario IntegrationScenario() => SpecScenario.Define(static spec => spec
        .WithSchema("Integration.Started", static schema => Arm(schema, "started"))
        .WithSchema("Integration.Stopped", static schema => Arm(schema, "stopped"))
        .WithSchema(Union, static schema => schema.AnyOf(
            static arm => arm.Ref("Integration.Started"),
            static arm => arm.Ref("Integration.Stopped")))
        .WithSchema("Integration.Feed", static schema => schema
            .Type("object")
            .Property("event", static property => property.Ref(Union), required: true))
        .WithOperation(OperationId, configure: static operation => operation
            .Response(200, "application/json", static schema => schema.Ref("Integration.Feed"))));

    private static void Arm(SchemaBuilder schema, string type) => schema
        .Type("object")
        .Property("type", property => property.Type("string").Enum(type), required: true);
}
