using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

/// <summary>
/// A <c>groups</c> row keyed by a dotted prefix owns the operations whose identifiers start with it,
/// the longest curated prefix wins, and the identifier keeps its name as its unprefixed form would.
/// </summary>
public sealed class OperationGroupPolicyTests
{
    [Test]
    [Arguments(true, "WidgetsClient")]
    [Arguments(false, "OpenCodeClient")]
    public async Task Bind_Should_Place_An_Operation_In_The_Family_Its_Longest_Curated_Prefix_Names(bool prefixRow, string client)
    {
        var groups = prefixRow ? Groups("lab.widget", ClientGroup("Widgets", null, null)) : Groups("lab", RootGroup());

        var plan = await BindAsync(groups, "lab.widget.get");

        await Assert.That(OwnerOf(plan, "lab.widget.get")).IsEqualTo(client);
    }

    [Test]
    public async Task Bind_Should_Prefer_The_Longer_Of_Two_Curated_Prefixes()
    {
        var groups = Groups("lab.widget", ClientGroup("Widgets", null, null));
        groups["lab.widget.part"] = ClientGroup("WidgetParts", null, null);

        var plan = await BindAsync(groups, "lab.widget.get", "lab.widget.part.get");

        await Assert.That(OwnerOf(plan, "lab.widget.part.get")).IsEqualTo("WidgetPartsClient");
        await Assert.That(OwnerOf(plan, "lab.widget.get")).IsEqualTo("WidgetsClient");
    }

    [Test]
    public async Task Bind_Should_Name_A_Prefix_Owned_Operation_As_Its_Unprefixed_Identity_Would()
    {
        var prefixed = (await BindAsync(Groups("lab.widget", ClientGroup("Widgets", null, null)), "lab.widget.get")).Clients
            .SelectMany(static client => client.Operations).Single();
        var unprefixed = (await BindAsync(Groups("widget", ClientGroup("Widgets", null, null)), "widget.get")).Clients
            .SelectMany(static client => client.Operations).Single();

        await Assert.That((prefixed.MethodName, prefixed.RouteMemberName, prefixed.Envelope?.ResponseTypeName))
            .IsEqualTo((unprefixed.MethodName, unprefixed.RouteMemberName, unprefixed.Envelope?.ResponseTypeName));
    }

    [Test]
    public async Task Bind_Should_Name_A_Prefix_Owned_Operations_Inline_Types_As_Its_Unprefixed_Identity_Would()
    {
        var prefixed = await BindAsync(Groups("lab.widget", ClientGroup("Widgets", null, null)), "lab.widget.get");
        var unprefixed = await BindAsync(Groups("widget", ClientGroup("Widgets", null, null)), "widget.get");

        await Assert.That(prefixed.Models.Select(static model => model.Name))
            .IsEquivalentTo(unprefixed.Models.Select(static model => model.Name), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("lab.wid")]
    [Arguments("lab.widget.get")]
    public async Task Apply_Should_Leave_An_Operation_A_Key_Does_Not_Prefix_At_A_Dot(string key)
    {
        var grouped = OperationGroupPolicy.Apply(await IngestAsync("lab.widget.get"), Groups(key, ClientGroup("Widgets", null, null)));

        var operation = grouped.Operations.Single();
        await Assert.That((operation.OperationId, operation.Group)).IsEqualTo(("lab.widget.get", "lab"));
        await Assert.That(operation.Segments).IsEquivalentTo(["lab", "widget", "get"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Apply_Should_Leave_A_Grouped_Document_As_It_Is()
    {
        var groups = Groups("lab.widget", ClientGroup("Widgets", null, null));
        var once = OperationGroupPolicy.Apply(await IngestAsync("lab.widget.get"), groups);

        var twice = OperationGroupPolicy.Apply(once, groups);

        await Assert.That(twice.Operations.Single().Segments).IsEquivalentTo(["widget", "get"], CollectionOrdering.Matching);
        await Assert.That(twice.Operations.Single().Group).IsEqualTo("lab.widget");
    }

    private static async Task<EmitPlan> BindAsync(Dictionary<string, GroupCuration> groups, params string[] operationIds) =>
        new BindingTestHost().Bind(await IngestAsync(operationIds), Selection(operationIds), Curation(groups));

    private static Task<SpecDocument> IngestAsync(params string[] operationIds) =>
        BindingTestHost.IngestAsync(SpecScenario.Define(spec =>
        {
            foreach (var operationId in operationIds)
            {
                _ = spec.WithOperation(operationId, path: "/api/" + operationId.Replace('.', '/'), configure: static operation => operation
                    .Response(200, "application/json", static schema => schema.Type("object")
                        .Property("id", static property => property.Type("string"), required: true)
                        .Property("detail", static property => property.Type("object")
                            .Property("note", static note => note.Type("string"), required: true))));
            }
        }));

    private static string OwnerOf(EmitPlan plan, string operationId) =>
        plan.Clients.Single(client => client.Operations.Any(operation => operation.OperationId == operationId)).Name;
}
