using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
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
    [Arguments("null", "queryParameterTypes[0] curation is null")]
    [Arguments("operationId", "queryParameterTypes[0] curation names no operationId")]
    [Arguments("parameter", "queryParameterTypes[0] curation names no parameter")]
    public async Task Check_Should_Defer_Unsafe_Rows_To_The_Binder(string fault, string problem)
    {
        var row = fault switch
        {
            "null" => null,
            "operationId" => QueryParameterType(" ", "limit", "int"),
            _ => QueryParameterType(Operation, " ", "int"),
        };
        // Null elements reach the row contract even though JSON member nullability is enforced.
        var curation = Curation(Groups("health", RootGroup()), queryParameterTypes: [row!]);
        var errors = new BindingErrorCollector();

        QueryParameterEvidencePolicy.Check(curation, Watch("packages/unwatched.ts"), errors);

        await Assert.That(errors.Count).IsEqualTo(0);
        var document = await ContractDocumentAsync();
        var exception = Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection("health.get"), curation));
        await Assert.That(exception.Errors.Single()).IsEqualTo(new BindingError(BindingErrorCategory.Curation,
            "queryParameterTypes[0]", problem));
    }

    [Test]
    public async Task Bind_Should_Type_A_Plain_String_Parameter_As_The_Integer_Its_Row_Names()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario());

        var plan = Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(Operation, "after", "long")],
            strings: [StringQueryParameter(Operation, "search")]);

        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(Kinds(plan)).IsEquivalentTo(["limit:Int32", "after:Int64", "search:Text"], CollectionOrdering.Any);
    }

    [Test]
    public async Task Bind_Should_Refuse_A_Plain_String_Parameter_No_Row_Classifies()
    {
        var document = await BindingTestHost.IngestAsync(WidgetScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(Operation, "after", "long")]));

        await Assert.That(exception.Errors.Select(static error => $"{error.Category}|{error.Subject}|{error.Problem}"))
            .IsEquivalentTo([$"Curation|{Operation}:search|{QueryParameterTypePolicy.UnclassifiedProblem}"], CollectionOrdering.Matching);
        await Assert.That(exception.Errors.Single().Kind).IsEqualTo(BindingErrorKind.MissingRow);
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

        // Member/type membership is the contract; declaration order is not asserted here.
        await Assert.That(Kinds(plan)).IsEquivalentTo(["order:ListOrder", "archived:BooleanText"], CollectionOrdering.Any);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Emit_Should_Map_Boolean_Text_To_Bool_Without_Changing_The_Route(bool reversed, bool required)
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithOperation(Operation, path: "/api/widget", configure: operation => operation
                .Parameter("archived", "query", schema => schema.Type("string").Enum(reversed ? ["false", "true"] : ["true", "false"]),
                    required: required)
                .WithoutResponse(200).Response(204))));
        var plan = Bind(document);

        var sources = SourceEmitter.Emit(plan);

        await Assert.That(Source(sources, "WidgetListRequest.cs")).Contains(required
            ? "public required bool Archived { get; init; }"
            : "public bool? Archived { get; init; }");
        await Assert.That(Source(sources, "OpenCodeRoutes.Widgets.cs")).Contains("query.AddBoolean(\"archived\", request.Archived);");
        await Assert.That(Kinds(plan)).IsEquivalentTo(["archived:BooleanText"], CollectionOrdering.Matching);
        await Assert.That(plan.Models).IsEmpty();
    }

    [Test]
    [Arguments("widget.list", "count", "the document now declares query parameter 'count' as a number; remove this row")]
    [Arguments("widget.list", "missing", "the operation has no query parameter 'missing'; remove this row")]
    [Arguments("widget.list", "mode", "query parameter 'mode' is no longer a plain string; remove this row")]
    [Arguments("widget.gone", "limit", "the operation no longer exists in the document; remove this row")]
    [Arguments("widget.other", "limit", "curated operation is not selected by the current profile")]
    public async Task Bind_Should_Refuse_A_Type_Row_With_Nothing_Left_To_Classify(string operationId, string parameter, string problem)
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            typed: [QueryParameterType(Operation, "limit", "int"), QueryParameterType(operationId, parameter, "int")]));

        var expected = new BindingError(BindingErrorCategory.Curation, operationId + ":" + parameter, problem);
        await Assert.That(exception.Errors.Single(error => error.Category == expected.Category && error.Subject == expected.Subject))
            .IsEqualTo(expected);
    }

    [Test]
    [Arguments("count", "the document now declares query parameter 'count' as a number; remove this row")]
    [Arguments("missing", "the operation has no query parameter 'missing'; remove this row")]
    [Arguments("mode", "query parameter 'mode' is no longer a plain string; remove this row")]
    public async Task Bind_Should_Refuse_A_String_Row_With_Nothing_Left_To_Classify(string parameter, string problem)
    {
        var curation = Curation(Groups("widget", ClientGroup("Widgets", null, null)),
            queryParameterTypes: [QueryParameterType(Operation, "limit", "int")],
            stringQueryParameters: [StringQueryParameter(Operation, parameter)]);
        var exception = await BindRefusalsAsync(RetirementScenario(), curation, Operation);

        await Assert.That(exception.Errors.Single(error => error.Category == BindingErrorCategory.Curation && error.Subject == Operation + ":" + parameter))
            .IsEqualTo(new BindingError(BindingErrorCategory.Curation, Operation + ":" + parameter, problem));
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
    public async Task Bind_Should_Refuse_A_Duplicated_String_Row()
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var exception = Assert.Throws<BindingException>(() => Bind(document,
            strings: [StringQueryParameter(Operation, "limit"), StringQueryParameter(Operation, "limit")]));

        await AssertRefusedAsync(exception, Operation + ":limit", "string query parameter curation is duplicated");
    }

    /// <summary>A required parameter keeps its requiredness through the type row: a required, non-nullable integer.</summary>
    [Test]
    public async Task Emit_Should_Write_A_Required_Typed_Parameter_As_A_Required_Integer()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(static spec => spec
            .WithOperation(Operation, path: "/api/widget", configure: static operation => operation
                .Parameter("lines", "query", static schema => schema.Type("string"), required: true)
                .WithoutResponse(200).Response(204))));
        var plan = Bind(document, typed: [QueryParameterType(Operation, "lines", "int")]);

        var record = Source(SourceEmitter.Emit(plan), "WidgetListRequest.cs");

        await Assert.That(record).Contains("public required int Lines { get; init; }");
    }

    [Test]
    [Arguments("short", "reason", "packages/x.ts", "anchor", "query parameter type 'short' is not 'int' or 'long'")]
    [Arguments("int", " ", "packages/x.ts", "anchor", "query parameter type curation must declare a reason")]
    [Arguments("int", "reason", " ", "anchor", "query parameter type curation must name its evidence: the upstream file and the text that declares the decode")]
    [Arguments("int", "reason", "packages/x.ts", " ", "query parameter type curation must name its evidence: the upstream file and the text that declares the decode")]
    [Arguments("int", "reason", "packages/x.ts", "anchor", "the query parameter is classified twice: a queryParameterTypes row types it, so its stringQueryParameters row must go", "double")]
    [Arguments("int", "reason", "packages/x.ts", "anchor", "the query parameter is a plain string that no curation row classifies: add a queryParameterTypes row when upstream decodes it as a number, or a stringQueryParameters row that says why it stays a string", "absent")]
    [Arguments("int", "reason", "packages/x.ts", "anchor", "the evidence 'packages/x.ts' is not in the source watch: pin it there, so a refresh that changes the decode stops for review", "watch")]
    [Arguments("int", "reason", "packages/x.ts", "anchor", "the operation no longer exists in the document; remove this row", "string", "widget.gone")]
    [Arguments("int", "reason", "packages/x.ts", "anchor", "curated operation is not selected by the current profile", "string", "widget.other")]
    public async Task Bind_Should_Refuse_Invalid_Query_Classification_Rows(string dotnetType, string reason, string evidencePath, string evidenceAnchor,
        string problem, string fault = "row", string operationId = Operation)
    {
        var document = await BindingTestHost.IngestAsync(RetirementScenario());

        var row = QueryParameterType(Operation, "limit", dotnetType, reason, evidencePath, evidenceAnchor);
        var errors = new BindingErrorCollector();
        var exception = fault == "watch" ? Assert.Throws<BindingException>(() =>
        {
            QueryParameterEvidencePolicy.Check(Curation(Groups(), queryParameterTypes: [row]), Watch("other.ts"), errors);
            errors.ThrowIfAny();
        }) : Assert.Throws<BindingException>(() => Bind(document,
            typed: fault switch { "absent" => [], "string" => [QueryParameterType(Operation, "limit", "int")], _ => [row] },
            strings: fault switch { "double" => [StringQueryParameter(Operation, "limit")], "string" => [StringQueryParameter(operationId, "limit")], _ => [] }));

        await Assert.That(exception.Errors.Single(error => error.Category == BindingErrorCategory.Curation && error.Subject == operationId + ":limit"))
            .IsEqualTo(new BindingError(BindingErrorCategory.Curation, operationId + ":limit", problem)
            {
                Kind = fault == "absent" ? BindingErrorKind.MissingRow : BindingErrorKind.Unspecified,
            });
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
        await Assert.That(exception.Errors).Contains(error => error.Category is BindingErrorCategory.Curation
                                                        && error.Subject == subject
                                                        && error.Problem.Contains(problem, StringComparison.Ordinal))
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
