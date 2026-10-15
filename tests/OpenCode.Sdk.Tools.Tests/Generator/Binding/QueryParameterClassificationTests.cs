using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// The two query-parameter classification channels (ADR-0037). A plain-string query parameter
/// binds as the integer a <c>queryParameterTypes</c> row names, stays a string under a
/// <c>stringQueryParameters</c> row, and refuses with neither. A row retires once the document no
/// longer gives it a plain string to classify, and a type row's evidence must be source-watched.
/// </summary>
public sealed class QueryParameterClassificationTests
{
    private const string Operation = "widget.list";

    [Test]
    public async Task Bind_Should_Type_A_Plain_String_Parameter_As_The_Integer_Its_Row_Names()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario());

        var plan = Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(Operation, "after", "long")],
            strings: [StringQueryParameter(Operation, "search")]);

        await Assert.That(Kinds(plan)).IsEquivalentTo(["limit:Int32", "after:Int64", "search:Text"]);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Plain_String_Parameter_No_Row_Classifies()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(Operation, "after", "long")]));

        await Assert.That(exception.Errors.Select(static error => $"{error.Category}|{error.Subject}|{error.Problem}"))
            .IsEquivalentTo([$"Curation|{Operation}:search|{QueryParameterTypePolicy.UnclassifiedProblem}"]);
    }

    /// <summary>An enum, a boolean text or a location selector is no plain string; it needs no row.</summary>
    [Test]
    public async Task Bind_Should_Ask_No_Row_For_A_Query_Parameter_That_Is_Not_A_Plain_String()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation(Operation, path: "/api/widget", configure: static operation => operation
                .Parameter("order", "query", static schema => schema.Type("string").Enum("asc", "desc"))
                .Parameter("archived", "query", static schema => schema.Type("string").Enum("true", "false"))
                .WithoutResponse(200).Response(204))));

        var plan = Bind(document);

        await Assert.That(Kinds(plan)).IsEquivalentTo(["order:ListOrder", "archived:BooleanText"]);
    }

    [Test]
    [Arguments("widget.list", "count", "now declares query parameter 'count' as a number; remove this row")]
    [Arguments("widget.list", "missing", "has no query parameter 'missing'; remove this row")]
    [Arguments("widget.list", "mode", "query parameter 'mode' is no longer a plain string; remove this row")]
    [Arguments("widget.gone", "limit", "no longer exists in the document; remove this row")]
    [Arguments("widget.other", "limit", "is not selected by the current profile")]
    public async Task Bind_Should_Refuse_A_Type_Row_With_Nothing_Left_To_Classify(string operationId, string parameter, string problem)
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(operationId, parameter, "int")]));

        await AssertRefusedAsync(exception, operationId + ":" + parameter, problem);
    }

    [Test]
    [Arguments("count", "now declares query parameter 'count' as a number; remove this row")]
    [Arguments("missing", "has no query parameter 'missing'; remove this row")]
    [Arguments("mode", "query parameter 'mode' is no longer a plain string; remove this row")]
    public async Task Bind_Should_Refuse_A_String_Row_With_Nothing_Left_To_Classify(string parameter, string problem)
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int")],
            strings: [StringQueryParameter(Operation, parameter)]));

        await AssertRefusedAsync(exception, Operation + ":" + parameter, problem);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Parameter_Classified_Twice()
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int")],
            strings: [StringQueryParameter(Operation, "limit")]));

        await AssertRefusedAsync(exception, Operation + ":limit", "is classified twice");
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Duplicated_Row()
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(Operation, "limit", "int")]));

        await AssertRefusedAsync(exception, Operation + ":limit", "query parameter type curation is duplicated");
    }

    [Test]
    [Arguments("short", "reason", "packages/x.ts", "anchor", "is not 'int' or 'long'")]
    [Arguments("int", " ", "packages/x.ts", "anchor", "must declare a reason")]
    [Arguments("int", "reason", " ", "anchor", "must name its evidence")]
    [Arguments("int", "reason", "packages/x.ts", " ", "must name its evidence")]
    public async Task Bind_Should_Refuse_An_Incomplete_Type_Row(string dotnetType, string reason, string evidencePath, string evidenceAnchor,
        string problem)
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", dotnetType, reason, evidencePath, evidenceAnchor)]));

        await AssertRefusedAsync(exception, Operation + ":limit", problem);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_String_Row_Without_A_Reason()
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            strings: [StringQueryParameter(Operation, "limit", reason: " ")]));

        await AssertRefusedAsync(exception, Operation + ":limit", "string query parameter curation must declare a reason");
    }

    /// <summary>The <c>ListRequest</c> spine's limit is an <see langword="int"/>; a limit that stays a string keeps the record flat.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Bind_Should_Derive_From_ListRequest_Only_When_The_Limit_Is_Typed(bool typed)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation(Operation, path: "/api/widget", configure: static operation => operation
                .Parameter("limit", "query", NullableString)
                .Parameter("order", "query", static schema => schema.Type("string").Enum("asc", "desc"))
                .Parameter("cursor", "query", NullableString)
                .WithoutResponse(200).Response(204))));

        var plan = Bind(document,
            typed: typed ? [QueryParameterType(Operation, "limit", "int")] : [],
            strings: typed ? [StringQueryParameter(Operation, "cursor")] : [StringQueryParameter(Operation, "limit"), StringQueryParameter(Operation, "cursor")]);

        await Assert.That(QueryRequest(plan).DerivesFromListRequest).IsEqualTo(typed);
    }

    [Test]
    public async Task Emit_Should_Write_A_Typed_Parameter_As_A_Nullable_Integer_Through_The_Integer_Builder()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario());
        var plan = Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(Operation, "after", "long")],
            strings: [StringQueryParameter(Operation, "search")]);

        var sources = SourceEmitter.Emit(plan);

        var record = Source(sources, "WidgetListRequest.cs");
        await Assert.That(record).Contains("public int? Limit { get; init; }");
        await Assert.That(record).Contains("public long? After { get; init; }");
        await Assert.That(record).Contains("public string? Search { get; init; }");
        var routes = Source(sources, "OpenCodeRoutes.Widgets.cs");
        await Assert.That(routes).Contains("query.AddInteger(\"limit\", request.Limit);");
        await Assert.That(routes).Contains("query.AddInteger(\"after\", request.After);");
        await Assert.That(routes).Contains("query.AddText(\"search\", request.Search);");
    }

    /// <summary>A missing classification row is curation the probe supplies no stand-in for, never a shape wall.</summary>
    [Test]
    public async Task Probe_Should_Mark_An_Operation_Bindable_When_Only_A_Classification_Row_Is_Missing()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario());
        var probe = new PendingOperationBindabilityProbe(new BindingTestHost().Binder);

        var marks = probe.Probe(document, [Operation]);

        await Assert.That(marks.Single().IsBindable).IsTrue();
    }

    [Test]
    public async Task Evidence_Should_Pass_When_The_Source_Watch_Names_Its_File()
    {
        var errors = new BindingErrorCollector();

        QueryParameterEvidencePolicy.Check(EvidenceCuration("packages/protocol/src/groups/widget.ts"), Watch("packages/protocol/src/groups/widget.ts"), errors);

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Evidence_Should_Refuse_A_File_The_Source_Watch_Does_Not_Name()
    {
        var errors = new BindingErrorCollector();

        QueryParameterEvidencePolicy.Check(EvidenceCuration("packages/protocol/src/groups/widget.ts"), Watch("packages/protocol/src/groups/other.ts"), errors);

        var exception = Assert.Throws<BindingException>(errors.ThrowIfAny);
        await AssertRefusedAsync(exception, Operation + ":limit", "is not in the source watch");
        await Assert.That(exception.Errors).Count().IsEqualTo(1);
    }

    private static EmitPlan Bind(SpecDocument document, IReadOnlyList<QueryParameterTypeCuration>? typed = null,
        IReadOnlyList<StringQueryParameterCuration>? strings = null) =>
        new BindingTestHost().Bind(
            document,
            Selection(Operation),
            Curation(Groups("widget", ClientGroup(clientName: "Widgets", handleName: null, handleParameter: null)),
                queryParameterTypes: typed,
                stringQueryParameters: strings));

    private static QueryRequestPlan QueryRequest(EmitPlan plan) =>
        plan.Clients.SelectMany(static client => client.Operations).Single().QueryRequest!;

    private static IEnumerable<string> Kinds(EmitPlan plan) =>
        QueryRequest(plan).Properties.Select(static property => $"{property.WireName}:{property.Kind}");

    private static string Source(IReadOnlyList<GeneratedSource> sources, string fileName) =>
        Encoding.UTF8.GetString(sources.Single(source => source.RelativePath.EndsWith(fileName, StringComparison.Ordinal)).Utf8Source.Span);

    private static async Task AssertRefusedAsync(BindingException exception, string subject, string problem) =>
        await Assert.That(exception.Errors.Any(error => error.Category is BindingErrorCategory.Curation
                                                        && error.Subject == subject
                                                        && error.Problem.Contains(problem, StringComparison.Ordinal)))
            .IsTrue()
            .Because(string.Join(" | ", exception.Errors.Select(static error => error.Subject + ": " + error.Problem)));

    private static void NullableString(SchemaBuilder schema) => schema.AnyOf(
        static branch => branch.Type("string"),
        static branch => branch.Type("null"));

    private static SpecScenario WidgetScenario() =>
        SpecScenario.Define(static spec => spec
            .WithOperation(Operation, path: "/api/widget", configure: static operation => operation
                .Parameter("limit", "query", NullableString)
                .Parameter("after", "query", static schema => schema.Type("string"))
                .Parameter("search", "query", NullableString)
                .WithoutResponse(200).Response(204)));

    /// <summary>One plain string to classify, one integer, one enum, and a second selectable operation.</summary>
    private static SpecScenario RetirementScenario() =>
        SpecScenario.Define(static spec => spec
            .WithOperation(Operation, path: "/api/widget", configure: static operation => operation
                .Parameter("limit", "query", NullableString)
                .Parameter("count", "query", static schema => schema.Type("integer"))
                .Parameter("mode", "query", static schema => schema.Type("string").Enum("a", "b"))
                .WithoutResponse(200).Response(204))
            .WithOperation("widget.other", path: "/api/widget/other", configure: static operation => operation
                .Parameter("limit", "query", NullableString)
                .WithoutResponse(200).Response(204)));

    private static GenerationCuration EvidenceCuration(string evidencePath) =>
        Curation(Groups("widget", RootGroup()), queryParameterTypes: [QueryParameterType(Operation, "limit", "int", evidencePath: evidencePath)]);

    private static SourceWatch Watch(string path) =>
        new()
        {
            SchemaVersion = 1,
            Sources =
            [
                new WatchedSource
                {
                    Path = path,
                    Sha256 = new string('0', 64),
                    Behavior = "Scenario behavior.",
                    Anchor = new SourceAnchor { Type = SourceAnchor.Contains, Text = "limit" },
                },
            ],
        };
}
