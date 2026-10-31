using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ListQueryEmissionTests
{
    /// <summary>
    /// A referenced enum, or an open enum spelled flat on both the value and the list item, emits one enum:
    /// the list item folds to the same values, so it adds no second type.
    /// </summary>
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    [Arguments(false, "WidgetMode")]
    [Arguments(true, "WidgetListRequestModes")]
    public async Task Emit_Should_Expose_An_Enum_List_And_Write_One_Key_Per_Element(bool flatOpenEnum, string enumName)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", static schema => schema.Type("object").Property("id", static property => property.Type("string"), required: true))
            .WithSchema("Widget.Mode", static schema => schema.Type("string").Enum("working", "branch"))
            .WithSchema("WidgetBadRequestError", static schema => schema.Type("object")
                .Property("_tag", static property => property.Type("string").Enum("WidgetBadRequestError"), required: true)
                .Property("message", static property => property.Type("string"), required: true))
            .WithOperation("widget.list", path: "/api/widget", configure: operation => operation
                .Parameter("modes", "query", schema => _ = flatOpenEnum
                    ? schema.AnyOf(
                        ModeValues,
                        RpcSpelling,
                        static branch => branch.Type("array").Items(static item => item.AnyOf(ModeValues, RpcSpelling)),
                        static branch => branch.Type("null"))
                    : schema.AnyOf(
                        static branch => branch.Ref("Widget.Mode"),
                        static branch => branch.Type("array").Items(static item => item.Ref("Widget.Mode")),
                        static branch => branch.Type("null")))
                .Response(200, "application/json", static schema => schema.Ref("WidgetInfo"))
                .Response(400, "application/json", static schema => schema.Ref("WidgetBadRequestError")))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.list"),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null))));

        var sources = SourceEmitter.Emit(plan);

        var request = EmitterSnapshot.Create([sources.Single(static source => source.RelativePath.EndsWith("WidgetListRequest.cs", StringComparison.Ordinal))]);
        var routes = EmitterSnapshot.Create([sources.Single(static source => source.RelativePath.EndsWith("OpenCodeRoutes.Widgets.cs", StringComparison.Ordinal))]);
        await Assert.That(request).Contains($"public IReadOnlyList<{enumName}>? Modes {{ get; init; }}");
        await Assert.That(sources.Where(static source => source.RelativePath.StartsWith("Widgets/Internal/Widget", StringComparison.Ordinal)
                && source.RelativePath.EndsWith("JsonConverter.cs", StringComparison.Ordinal))
            .Select(static source => source.RelativePath))
            .IsEquivalentTo([$"Widgets/Internal/{enumName}JsonConverter.cs"], CollectionOrdering.Matching);
        await Assert.That(routes).Contains("query.AddTextList(\"modes\", request.Modes?.Select(static item => ToWireValue(item)));");
        await Assert.That(await GeneratedSourceCompiler.CompileWithSdkCoreAsync(sources)).IsEmpty();
    }

    private static void ModeValues(SchemaBuilder branch) => branch.Type("string").Enum("working", "branch");

    private static void RpcSpelling(SchemaBuilder branch) => branch.Type("string").Pattern("^rpc\\.[\\s\\S]*?$");
}
