using Microsoft.CodeAnalysis;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class PaginationMethodEmitterTests
{
    [Test]
    [Arguments("upstream", WidgetsRemark + WidgetsReference + "List widgets. Retrieve widgets in &apos;stored&apos; order.")]
    [Arguments("multiline", WidgetsRemark + WidgetsReference + "List widgets. Retrieve widgets in &apos;stored&apos; order.")]
    [Arguments("curated", WidgetsRemark + WidgetsReference + "Browse the <c>widget</c> catalog &amp; its pages.")]
    [Arguments("remarks-only", WidgetsRemark + " Pass <c>cursor.next</c> back unchanged." + WidgetsReference + "List widgets.")]
    [Arguments("fallback", WidgetsRemark + WidgetsReference + "Calls the &apos;/api/widget&apos; operation.")]
    [Arguments("handle", "Operation <c>widget.item.list</c>: <c>GET /api/widget/{widgetID}/item</c>."
        + " See <see cref=\"ListItemsAsync(WidgetItemListRequest, OpenCodeRequestOptions, CancellationToken)\" />. "
        + "List widget items. Items keep the requested order.")]
    public async Task Emit_Should_Document_The_Page_Operation_And_Effective_Summary(string row, string remarks)
    {
        var handle = row == "handle";
        var operationId = handle ? "widget.item.list" : "widget.list";
        var route = handle ? "/api/widget/{widgetID}/item" : "/api/widget";
        var document = await BindingTestHost.IngestAsync(CursorListScenario(path: route, operationId: operationId, configure: operation =>
        {
            _ = row switch
            {
                "upstream" => operation.Summary("List widgets").Description("Retrieve widgets in 'stored' order."),
                "multiline" => operation.Summary("List widgets").Description("Retrieve widgets\n  in 'stored' order."),
                "handle" => operation.Summary("List widget items").Description("Items keep the requested order.")
                    .Parameter("widgetID", "path", static schema => schema.Type("string"), required: true),
                "fallback" => operation,
                _ => operation.Summary("List widgets."),
            };
            _ = operation.Parameter("limit", "query", static schema => schema.AnyOf(static arm => arm.Type("string"), static arm => arm.Type("null")))
                .Parameter("order", "query", static schema => schema.AnyOf(static arm => arm.Type("string").Enum("asc", "desc"), static arm => arm.Type("null")))
                .Parameter("cursor", "query", static schema => schema.AnyOf(static arm => arm.Type("string"), static arm => arm.Type("null")));
        }));
        DescriptionCuration[] rows = row switch
        {
            "curated" => [Description("WidgetsClient", "ListWidgetsAsync", "Browse the `widget` catalog & its pages.")],
            "remarks-only" => [Description("WidgetsClient", "ListWidgetsAsync", remarks: "Pass `cursor.next` back unchanged.") with { Summary = null }],
            _ => [],
        };
        var curation = Curation(Groups("widget", ClientGroup("Widgets", handle ? "WidgetClient" : null, handle ? "widgetID" : null)),
            queryParameterTypes: [QueryParameterType(operationId, "limit", "int")], stringQueryParameters: [StringQueryParameter(operationId, "cursor")]);
        var plan = new BindingTestHost().Bind(document, Selection(operationId), curation with { Descriptions = rows });
        var bound = plan.Clients.SelectMany(static client => client.Operations).Single(static operation => operation.Pagination is not null);

        var method = PaginationMethodEmitter.Emit(bound, EmissionMode.Public);

        await Assert.That(method.GetLeadingTrivia().ToFullString()).IsEqualTo(Documentation("GET " + route,
            handle ? "WidgetItemListResponse" : "WidgetListResponse", remarks));
        await Assert.That(method.WithLeadingTrivia().NormalizeWhitespace(eol: "\n").ToFullString())
            .IsEqualTo(handle ? HandleDeclaration : WidgetsDeclaration);
    }

    private const string WidgetsRemark = "Operation <c>widget.list</c>: <c>GET /api/widget</c>.";
    private const string WidgetsReference = " See <see cref=\"ListWidgetsAsync(WidgetListRequest, OpenCodeRequestOptions, CancellationToken)\" />. ";

    private const string WidgetsDeclaration =
        "public virtual CursorSequence<WidgetListResponse, WidgetInfo> EnumerateWidgetsAsync(WidgetListRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)\n"
        + "{\n"
        + "    return CursorPaginator.EnumerateAsync(ListWidgetsAsync, request, WidgetListResponseAdapter.Instance, requestOptions, cancellationToken);\n"
        + "}";

    private const string HandleDeclaration =
        "public virtual CursorSequence<WidgetItemListResponse, WidgetInfo> EnumerateItemsAsync(WidgetItemListRequest? request = null, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)\n"
        + "{\n"
        + "    return CursorPaginator.EnumerateAsync(ListItemsAsync, request, WidgetItemListResponseAdapter.Instance, requestOptions, cancellationToken);\n"
        + "}";

    private static string Documentation(string route, string page, string remarks) =>
        "/// <summary>\n"
        + $"/// Enumerates the items returned by &apos;{route}&apos; by following each opaque next cursor; every filter of the first request rides each continuation.\n"
        + "/// </summary>\n"
        + "/// <param name=\"request\">The request shaping the first page.</param>\n"
        + "/// <param name=\"requestOptions\">The per-call options applied to every page; NoThrow is refused.</param>\n"
        + "/// <param name=\"cancellationToken\">The cancellation token.</param>\n"
        + $"/// <returns>The &apos;WidgetInfo&apos; sequence, whose Pages expose each &apos;{page}&apos;.</returns>\n"
        + "/// <exception cref=\"OpenCodeApiException\">The API returned an error status; pagination API errors always throw.</exception>\n"
        + "/// <exception cref=\"OpenCodeTransportException\">The server could not be reached or returned a malformed success body.</exception>\n"
        + "/// <remarks>\n"
        + $"/// {remarks}\n"
        + "/// </remarks>\n";
}
