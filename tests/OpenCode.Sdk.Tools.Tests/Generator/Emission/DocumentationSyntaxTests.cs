using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Binding.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class DocumentationSyntaxTests
{
    [Test]
    public async Task EnvelopeEmitter_Should_Keep_Wire_Summary_Backticks_Plain_When_Only_A_Member_Is_Curated()
    {
        var document = await BindingTestHost.IngestAsync(SpecScenario.Define(spec => spec
            .WithSchema("WidgetInfo", static schema => schema.Type("object").Property("id", static property => property.Type("string"), required: true))
            .WithOperation("widget.get", configure: static operation => operation.Response(200, "application/json", static schema => schema
                .Type("object").Description("Keep `wire` unchanged.")
                .Property("item", static property => property.Ref("WidgetInfo"), required: true)))));
        var plan = new BindingTestHost().Bind(document, Selection("widget.get"), Curation(Groups("widget", RootGroup())) with
        {
            Descriptions = [Description("WidgetResponse", "Item", "The curated payload.")],
        });

        var source = EnvelopeEmitter.Emit(plan.Clients, plan.Namespaces).Single(static source => source.RelativePath.EndsWith("/WidgetResponse.cs", StringComparison.Ordinal));

        await Assert.That(Unindented(Encoding.UTF8.GetString(source.Utf8Source.Span))).Contains(
            "/// <summary>\n/// Keep `wire` unchanged.\n/// </summary>\npublic sealed record WidgetResponse");
    }

    [Test]
    [Arguments("WidgetListRequest.cs", "/// <remarks>\n/// &apos;limit&apos;: Keep `wire` unchanged. Read <c>cursor.next</c>.\n/// </remarks>\npublic sealed record WidgetListRequest")]
    [Arguments("WidgetsClient.cs", "/// <summary>\n/// List `widgets`.\n/// </summary>\n/// <param name = \"request\">The request shaping the query.</param>")]
    [Arguments("WidgetsClient.cs", "/// <remarks>\n/// Operation <c>widget.list</c>: <c>GET /api/widget</c>. See <see cref = \"ListWidgetsAsync(WidgetListRequest, OpenCodeRequestOptions, CancellationToken)\"/>. List `widgets`.\n/// </remarks>\npublic virtual CursorSequence<WidgetListResponse, WidgetInfo> EnumerateWidgetsAsync(")]
    public async Task SourceEmitter_Should_Keep_Wire_Backticks_Plain_Beside_Curated_Query_Remarks(string path, string expected)
    {
        var document = await CursorListDocumentAsync(static operation => operation.Summary("List `widgets`."),
            static schema => schema.Type("string").Description("Keep `wire` unchanged."));
        var curation = CursorListCuration();
        var plan = new BindingTestHost().Bind(document, Selection("widget.list"), curation with
        {
            Descriptions = [Description("WidgetListRequest", remarks: "Read `cursor.next`.")],
        });

        var source = SourceEmitter.Emit(plan).Single(source => source.RelativePath.EndsWith("/" + path, StringComparison.Ordinal));

        await Assert.That(Unindented(Encoding.UTF8.GetString(source.Utf8Source.Span))).Contains(expected);
    }

    [Test]
    [Arguments("Use `a` and `b`.", "Use <c>a</c> and <c>b</c>.")]
    [Arguments("`start` to `end`", "<c>start</c> to <c>end</c>")]
    [Arguments("A \"`<x & y>`\" & 'z'.", "A &quot;<c>&lt;x &amp; y&gt;</c>&quot; &amp; &apos;z&apos;.")]
    [Arguments("Pairs `a` then ` stays.", "Pairs <c>a</c> then ` stays.")]
    [Arguments(" Keep\n `one  run`  here. ", "Keep <c>one run</c> here.")]
    public async Task DocumentedType_Should_Emit_Curated_Backticks_As_Escaped_Code(string curated, string rendered)
    {
        var declaration = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), "Probe");
        var documentation = new TypeDocumentationPlan
        {
            Summary = curated,
            SummaryIsCurated = true,
            Remarks = "Wire `raw` & text. " + curated,
            CuratedRemarks = curated,
        };

        var result = DocumentationSyntax.DocumentedType(declaration, documentation).GetLeadingTrivia().ToFullString();

        await Assert.That(result).IsEqualTo($"/// <summary>\n/// {rendered}\n/// </summary>\n/// <remarks>\n/// Wire `raw` &amp; text. {rendered}\n/// </remarks>\n");
    }

    [Test]
    [Arguments("model", "/Notice.cs", "/// <summary>\n/// The <c>notice.changed</c> event.\n/// </summary>\n/// <remarks>\n/// Read <c>data</c> first.\n/// </remarks>\npublic sealed record Notice")]
    [Arguments("member", "/Notice.cs", "/// <summary>\n/// The <c>data</c> payload.\n/// </summary>\n/// <remarks>\n/// Holds <c>text</c>.\n/// </remarks>\n[JsonPropertyName(\"data\")]\npublic required Details Data")]
    [Arguments("operation", "OpenCodeClient.cs", "/// Operation <c>event.get</c>: <c>GET /api/x</c>. Reconnect after <c>server.connected</c>.\n/// </remarks>\npublic virtual IAsyncEnumerable<IEvents> GetEventAsync(")]
    [Arguments("page-summary", "WidgetsClient.cs", "/// <summary>\n/// Browse the <c>widget</c> catalog &amp; its pages.\n/// </summary>\n")]
    public async Task SourceEmitter_Should_Emit_Curated_Inline_Code_On_Models_Members_And_Operations(string row, string path, string expected)
    {
        var page = row == "page-summary";
        var document = page ? await CursorListDocumentAsync(null, static schema => schema.Type("string"))
            : await BindingTestHost.IngestAsync(new EventDocumentationScenario());
        DescriptionCuration[] rows = row switch
        {
            "model" => [Description("Notice", null, "The `notice.changed` event.", "Read `data` first.")],
            "member" => [Description("Notice", "Data", "The `data` payload.", "Holds `text`.")],
            "operation" => [Description("OpenCodeClient", "GetEventAsync", remarks: "Reconnect after `server.connected`.") with { Summary = null }],
            "page-summary" => [Description("WidgetsClient", "ListWidgetsAsync", "Browse the `widget` catalog & its pages.")],
            _ => throw new InvalidOperationException("Unknown inline-code row: " + row),
        };
        var plan = new BindingTestHost().Bind(document, Selection(page ? "widget.list" : "event.get"),
            page ? CursorListCuration() with { Descriptions = rows } : EventCuration(rows));
        var source = SourceEmitter.Emit(plan).Single(source => source.RelativePath.EndsWith(path, StringComparison.Ordinal));

        await Assert.That(Unindented(Encoding.UTF8.GetString(source.Utf8Source.Span))).Contains(expected);
    }

    [Test]
    [ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
    public async Task MemberDocumentation_Should_Emit_The_Reconnect_Remark_With_Its_Event_As_Code(PinnedBinding pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        var source = ClientEmitter.Emit(pinned.Plan.Clients, pinned.Plan.Namespaces)
            .Single(static source => source.RelativePath == "Events/EventsClient.cs");

        await Assert.That(Unindented(Encoding.UTF8.GetString(source.Utf8Source.Span))).Contains(
            "/// <remarks>\n"
            + "/// Operation <c>event.subscribe</c>: <c>GET /api/event</c>. The stream reconnects after a failure, with a new attempt 1 s later and no backoff."
            + " It does not complete on its own; caller cancellation or root-client disposal ends it."
            + " After 1 minute in which every attempt fails, it throws the last failure."
            + " A repeated <c>server.connected</c> event means events published in between were missed; refresh authoritative state.\n"
            + "/// </remarks>\n"
            + "public virtual IAsyncEnumerable<IEvent> SubscribeAsync(");
    }

    [Test]
    [Arguments("A <curated> & value.", "/// <summary>\n/// A &lt;curated&gt; &amp; value.\n/// </summary>\n")]
    [Arguments(" \tGets the\r\n  preserved\t raw JSON payload. \n", "/// <summary>\n/// Gets the preserved raw JSON payload.\n/// </summary>\n")]
    [Arguments("Gets the '\"<&>' wire value.", "/// <summary>\n/// Gets the &apos;&quot;&lt;&amp;&gt;&apos; wire value.\n/// </summary>\n")]
    public async Task Documentation_Should_Normalize_And_Escape_Summaries(string summary, string expected)
    {
        await Assert.That(DocumentationSyntax.Documentation(summary).ToFullString()).IsEqualTo(expected);
        await Assert.That(DocumentationSyntax.MemberDocumentation(summary, []).ToFullString()).IsEqualTo(expected);
    }

    [Test]
    public async Task MemberDocumentation_Should_Preserve_Code_Runs_In_Operation_Remarks()
    {
        DocumentationRun[] remarks =
        [
            DocumentationRun.Prose("Operation "), DocumentationRun.Code("example.list"),
            DocumentationRun.Prose(": "), DocumentationRun.Code("GET /example/{id}"), DocumentationRun.Prose("."),
        ];
        var rendered = DocumentationSyntax.MemberDocumentation("List the widgets", [], remarks: remarks).ToFullString();
        await Assert.That(rendered).IsEqualTo("/// <summary>\n/// List the widgets\n/// </summary>\n/// <remarks>\n/// Operation <c>example.list</c>: <c>GET /example/{id}</c>.\n/// </remarks>\n");
    }

    [Test]
    public async Task MemberDocumentation_Should_Render_All_Sections_And_Escape_Code()
    {
        var rendered = DocumentationSyntax.MemberDocumentation("Gets the value.",
            [new DocumentedParameter("value", " The\n wire <value>. ")], " The & result. ",
            [new DocumentedException("JsonException", " Invalid \t'value'. ")],
            [DocumentationRun.Prose("Use "), DocumentationRun.Code("\"<&>\""), DocumentationRun.Prose(" & keep  spacing.")]).ToFullString();
        await Assert.That(rendered).IsEqualTo("/// <summary>\n/// Gets the value.\n/// </summary>\n/// <param name=\"value\">The wire &lt;value&gt;.</param>\n/// <returns>The &amp; result.</returns>\n/// <exception cref=\"JsonException\">Invalid &apos;value&apos;.</exception>\n/// <remarks>\n/// Use <c>&quot;&lt;&amp;&gt;&quot;</c> &amp; keep  spacing.\n/// </remarks>\n");
    }
}
