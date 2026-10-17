using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

public sealed class QueryRequestFacetBinderTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Refuse_The_Whole_Facet_When_One_Parameter_Is_Unclassified(bool multiple)
    {
        var context = await CreateContextAsync(operation =>
        {
            operation.Parameter("good", "query", static schema => schema.Type("string").Enum("true", "false"))
                .Parameter("search", "query", static schema => schema.Type("string"));
            if (multiple)
            {
                operation.Parameter("bad", "query", static schema => schema.Type("array").Items(static item => item.Type("string")));
            }
        });
        var result = new QueryRequestFacetBinder(context).Bind();
        await Assert.That(result).IsTypeOf<FacetResult<QueryRequestPlan?>.Refused>();
        var expected = new List<BindingError>
        {
            new(BindingErrorCategory.Curation, "widget.list:search",
                "the query parameter is a plain string that no curation row classifies: add a queryParameterTypes row when upstream decodes it as a number, or a stringQueryParameters row that says why it stays a string") { Kind = BindingErrorKind.MissingRow },
        };
        if (multiple)
        {
            expected.Add(new BindingError(BindingErrorCategory.Operation, "widget.list", "query parameter 'bad' has an unsupported schema shape"));
        }
        await Assert.That(((FacetResult<QueryRequestPlan?>.Refused)result).Errors).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(context.Errors.Snapshot()).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Refuse_An_Enum_Without_A_Generated_Model(bool referencedNullable)
    {
        var context = referencedNullable
            ? await CreateContextAsync(static operation => operation.Parameter("state", "query", static schema => schema.Ref("State")),
                static spec => spec.WithSchema("State", static schema => schema.AnyOf(
                    static branch => branch.Type("string").Enum("open", "closed"), static branch => branch.Type("null"))))
            : await CreateContextAsync(static operation => operation.Parameter("state", "query",
                static schema => schema.Type("string").Enum("open", "closed")));
        var result = new QueryRequestFacetBinder(context).Bind();
        await Assert.That(result).IsTypeOf<FacetResult<QueryRequestPlan?>.Refused>();
        await Assert.That(((FacetResult<QueryRequestPlan?>.Refused)result).Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Operation, "widget.list", "query parameter 'state' binds an enum that has no generated model"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Bind_Should_Keep_A_Referenced_Nullable_Enum_Refused(bool fullPipeline)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithSchema("State", static schema => schema.AnyOf(
                static branch => branch.Type("string").Enum("open", "closed"), static branch => branch.Type("null")))
            .WithOperation("widget.list", configure: static operation => operation
                .WithoutResponse(200).Response(204)
                .Parameter("state", "query", static schema => schema.Ref("State")))));
        if (!fullPipeline)
        {
            await Assert.That(QueryEnumShapePolicy.ResolveModelKey(new RefNode { Target = "State" }, document.Schemas)).IsNull();
            return;
        }

        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document,
            Selection("widget.list"), Curation(Groups("widget", RootGroup()))));
        await Assert.That(exception.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Operation, "widget.list", "query parameter 'state' binds an enum that has no generated model"),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Distinguish_An_Absent_Query_From_A_Refused_Query()
    {
        var absent = new QueryRequestFacetBinder(await CreateContextAsync(static _ => { })).Bind();
        await Assert.That(absent).IsTypeOf<FacetResult<QueryRequestPlan?>.Bound>();
        await Assert.That(((FacetResult<QueryRequestPlan?>.Bound)absent).Value).IsNull();
        var refused = new QueryRequestFacetBinder(await CreateContextAsync(static operation => operation
            .Parameter("bad", "query", static schema => schema.Type("integer")))).Bind();
        await Assert.That(refused).IsTypeOf<FacetResult<QueryRequestPlan?>.Refused>();
        await Assert.That(((FacetResult<QueryRequestPlan?>.Refused)refused).Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Operation, "widget.list", "query parameter 'bad' has an unsupported schema shape"),
        ], CollectionOrdering.Matching);
    }

    private static async Task<OperationFacetContext> CreateContextAsync(Action<OperationBuilder> configure,
        Action<SpecDocumentBuilder>? configureSchemas = null)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec =>
        {
            configureSchemas?.Invoke(spec);
            spec.WithOperation("widget.list", configure: configure);
        }));
        var errors = new BindingErrorCollector();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        return new OperationFacetContext(document, document.Operations.Single(), Curation(Groups("widget", RootGroup())), names, errors,
            new TypePlanBinder(document.Schemas, names, errors));
    }
}
