using System.Globalization;
using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class DescriptionInventoryTests(PinnedBinding pinned)
{
    private static readonly string[] OperationSubject = ["OpenCodeClient.GetPingAsync"];

    [Test]
    [Arguments("overlap")]
    [Arguments("upstream-doc")]
    public async Task Descriptions_Should_Match_Reviewed_Inventories(string inventory)
    {
        var fileSystem = new RealFileSystem();
        var root = new RepositoryRoot(fileSystem).Locate();
        var lines = await fileSystem.File.ReadAllLinesAsync(fileSystem.Path.Combine(root,
            "tests/OpenCode.Sdk.Tools.Tests/Generator/Binding/description-" + inventory + ".txt"), CancellationToken.None);
        var expected = lines.Where(static line => line.Length > 0 && line[0] != '#').Select(static line => line.Split(" - ", StringSplitOptions.None)[0]);
        var actual = (inventory == "overlap" ? Overlap(pinned.Plan, pinned.Curation.Descriptions)
            : await UndescribedAsync(fileSystem, root, pinned.Curation.Descriptions)).ToArray();
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        TestContext.Current!.Output.WriteLine($"{inventory}: {actual.Length} rows (bound pin).");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Overlap_Should_Select_Operation_Descriptions_Not_Summaries(bool described)
    {
        var plan = EmitterPlanFixture.Create();
        var client = plan.Clients[0];
        plan = plan with { Clients = [client with { Operations = [client.Operations[0] with { Summary = "Always present.", Description = described ? "Projected description." : null }] }] };
        await Assert.That(Overlap(plan, [Row()])).IsEquivalentTo(described ? OperationSubject : [], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("description: upstream now describes it", false)]
    [Arguments("  /** upstream now describes it", false)]
    [Arguments("An undescribed declaration.", true)]
    public async Task Undescribed_Should_Detect_Both_Inventory_Changes(string declaration, bool undescribed)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().WithFile("external/opencode/packages/example.ts").Which(file => file.HasStringContent(declaration));
        var actual = await UndescribedAsync(fileSystem, fileSystem.Directory.GetCurrentDirectory(), [Row()]);
        await Assert.That(actual).IsEquivalentTo(undescribed ? OperationSubject : [], CollectionOrdering.Matching);
        await Assert.That(actual).IsNotEquivalentTo(undescribed ? [] : OperationSubject);
        await Assert.That(await UndescribedAsync(fileSystem, fileSystem.Directory.GetCurrentDirectory(),
            [Row() with { Sources = [new() { Canon = "docs/architecture/example.md#contract", Excerpt = "The SDK owns it." }] }])).IsEmpty();
    }

    private static DescriptionCuration Row() => new()
    {
        Type = "OpenCodeClient",
        Member = "GetPingAsync",
        Summary = "The curated ping.",
        Reason = "Prove the inventory guards.",
        Sources = [new() { Location = "packages/example.ts:1", Excerpt = "An explicit declaration." }],
    };

    [Test]
    [Arguments("SessionDiffRequest", "Context", "SessionDiffRequest.Context")]
    [Arguments("IDurableEnvelope", null, null)]
    [Arguments("IDurableEnvelope", "Seq", "IDurableEnvelope.Seq")]
    [Arguments("IDurableEnvelope", "Version", "IDurableEnvelope.Version")]
    [Arguments("WidgetListResponse", "Cursor", null)]
    [Arguments("WidgetListResponse", "Widgets", null)]
    [Arguments("WidgetSpotResponse", "Location", null)]
    [Arguments("WidgetSpotResponse", "Spot", null)]
    [Arguments("WidgetBinaryResponse", "ContentType", null)]
    [Arguments("WidgetBinaryResponse", "Content", null)]
    public async Task Overlap_Should_Count_Only_Upstream_Target_Descriptions(string type, string? member, string? expected)
    {
        var plan = type == "SessionDiffRequest" ? pinned.Plan : EmitterPlanFixture.Create();
        plan = plan with { HoistedInterfaces = EmitterPlanFixture.CreateHoistPlan().HoistedInterfaces };
        var client = plan.Clients[0];
        var operation = client.Operations[0];
        var binary = operation with
        {
            Envelope = operation.Envelope! with
            {
                ResponseTypeName = "WidgetBinaryResponse",
                Kind = EnvelopeKind.Binary,
                PayloadName = "Content",
                PayloadType = new BinaryTypeReferencePlan { IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull },
            },
        };
        plan = plan with { Clients = [.. plan.Clients.Skip(1), client with { Operations = [.. client.Operations, binary] }] };
        await Assert.That(Overlap(plan, [Row() with { Type = type, Member = member }]))
            .IsEquivalentTo(expected is null ? [] : new[] { expected }, CollectionOrdering.Matching);
    }

    private static string Subject(DescriptionCuration row) => row.Type + (row.Member is null ? "" : "." + row.Member);
    private static IEnumerable<string> Overlap(EmitPlan plan, IReadOnlyList<DescriptionCuration> rows) => rows.Where(row => !string.IsNullOrWhiteSpace(Projected(plan, row))).Select(Subject);
    private static string? Projected(EmitPlan plan, DescriptionCuration row)
    {
        var model = plan.Models.SingleOrDefault(model => model.Name == row.Type);
        var union = plan.Unions.SingleOrDefault(union => union.Name == row.Type);
        var carrier = plan.HoistedInterfaces.SingleOrDefault(carrier => carrier.Name == row.Type);
        if (row.Member is null) { return model?.Description ?? union?.Description; }
        return (model as ObjectModelPlan)?.Properties.SingleOrDefault(property => property.Name == row.Member)?.Description
            ?? plan.Clients.SelectMany(static client => client.Operations).Select(static operation => operation.QueryRequest)
                .OfType<QueryRequestPlan>().Where(query => query.TypeName == row.Type).SelectMany(static query => query.Properties)
                .SingleOrDefault(property => !property.IsInherited && property.Kind is not QueryValueKind.RouteTail && property.PropertyName == row.Member)?.Description
            ?? union?.HoistedMembers.SingleOrDefault(property => property.Name == row.Member)?.Description
            ?? carrier?.Members.SingleOrDefault(property => property.Name == row.Member)?.Description
            ?? plan.Clients.SingleOrDefault(client => client.Name == row.Type)?.Operations.SingleOrDefault(operation => operation.MethodName == row.Member)?.Description;
    }
    private static async Task<IReadOnlyList<string>> UndescribedAsync(IFileSystem fileSystem, string root, IReadOnlyList<DescriptionCuration> rows)
    {
        var result = new List<string>();
        foreach (var row in rows.Where(static row => row.Sources!.Any(static source => source?.Location is not null) && !row.Sources!.Any(static source => source?.Canon is not null)))
        {
            var markers = new List<string>();
            foreach (var source in row.Sources!.OfType<DescriptionSource>()) // The binder validates committed source shapes.
            {
                var parts = source.Location!.Split(':');
                var line = int.Parse(parts[1], CultureInfo.InvariantCulture);
                var lines = await fileSystem.File.ReadAllLinesAsync(fileSystem.Path.Combine(root, "external/opencode", parts[0]), CancellationToken.None);
                markers.AddRange(lines.Skip(Math.Max(0, line - 6)).Take(line + 5 - Math.Max(0, line - 6))
                    .Where(static text => text.Contains("description:", StringComparison.Ordinal) || text.TrimStart().StartsWith("/**", StringComparison.Ordinal)));
            }
            if (markers.Count == 0) { result.Add(Subject(row)); }
        }
        return result;
    }
}
