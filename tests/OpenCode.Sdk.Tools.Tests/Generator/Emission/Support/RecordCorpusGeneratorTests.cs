using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

public sealed class RecordCorpusGeneratorTests
{
    [Test]
    public async Task GenerateAsync_Should_Populate_Collections_And_Each_Union_Branch()
    {
        var plan = EmitterPlanFixture.Create() with
        {
            Models = [Record("Root", Property("items", new ListTypeReferencePlan { ElementType = Named("Child"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull }, true),
                Property("lookup", new DictionaryTypeReferencePlan { ValueType = Named("Child"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull }, true),
                Property("choice", Named("IChoice"), true), Property("ab", Named("long"), true), Property("ba", Named("long"), true),
                Property("ac", SpecialNumber(), true), Property("ca", SpecialNumber(), true),
                Property("branches", new ListTypeReferencePlan { ElementType = Named("IChoice"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull }, false)),
                Record("Child", Property("value", Named("string"), true)), Variant("Second", "second"), Variant("First", "first")],
            Unions = [Union("IChoice", Arm("Second"), Arm("First"))],
        };
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, ["Root"]);
        await Assert.That(rows.Where(static row => row.IsSeed).Select(static row => row.Name)).IsEquivalentTo([
            "Root/minimal", "Root/maximal", "Root/branch/Root.choice/Second/minimal", "Root/branch/Root.choice/Second/maximal",
            "Root/branch/Root.choice/First/minimal", "Root/branch/Root.choice/First/maximal",
            "Root/branch/Root.branches[]/Second/minimal", "Root/branch/Root.branches[]/Second/maximal",
            "Root/branch/Root.branches[]/First/minimal", "Root/branch/Root.branches[]/First/maximal",
        ], CollectionOrdering.Matching);
        using var minimal = JsonDocument.Parse(rows[0].Json);
        using var maximal = JsonDocument.Parse(rows[1].Json);
        await Assert.That(minimal.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(minimal.RootElement.GetProperty("lookup").EnumerateObject().Select(static member => member.Name)).IsEmpty();
        await Assert.That(maximal.RootElement.GetProperty("items")[0].GetProperty("value").GetString()).IsEqualTo("Root.items[].value");
        await Assert.That(maximal.RootElement.GetProperty("lookup").GetProperty("entry").GetProperty("value").GetString()).IsEqualTo("Root.lookup.entry.value");
        await Assert.That(maximal.RootElement.GetProperty("ab").GetInt64()).IsNotEqualTo(maximal.RootElement.GetProperty("ba").GetInt64());
        await Assert.That(maximal.RootElement.GetProperty("ac").GetDouble()).IsNotEqualTo(maximal.RootElement.GetProperty("ca").GetDouble());
        using var branch = JsonDocument.Parse(rows.Single(static row => row.Name == "Root/branch/Root.choice/First/minimal").Json);
        await Assert.That(branch.RootElement.GetProperty("choice").GetProperty("type").GetString()).IsEqualTo("first");
        using var listBranch = JsonDocument.Parse(rows.Single(static row => row.Name == "Root/branch/Root.branches[]/First/minimal").Json);
        await Assert.That(listBranch.RootElement.GetProperty("branches").EnumerateArray().Select(static item => item.GetProperty("type").GetString()))
            .IsEquivalentTo((string?[])["first"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GenerateAsync_Should_Reach_Every_Nested_Site_With_Its_Ancestor_Markers()
    {
        var plan = EmitterPlanFixture.CreateHoistPlan();
        plan = plan with { Models = [.. plan.Models, Record("Root", Property("choice", Named("IExampleEvent"), true))] };
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, ["Root"]);
        await Assert.That(rows.Where(static row => row.IsSeed).Select(static row => row.Name)).IsEquivalentTo([
            "Root/minimal", "Root/maximal",
            "Root/branch/Root.choice/CreatedEvent/minimal", "Root/branch/Root.choice/CreatedEvent/maximal",
            "Root/branch/Root.choice/DeletedEvent/minimal", "Root/branch/Root.choice/DeletedEvent/maximal",
            "Root/branch/Root.choice/IExamplePhase/minimal", "Root/branch/Root.choice/IExamplePhase/maximal",
            "Root/branch/Root.choice/IExamplePhase/PhaseStartedEvent/minimal", "Root/branch/Root.choice/IExamplePhase/PhaseStartedEvent/maximal",
            "Root/branch/Root.choice/IExamplePhase/PhaseEndedEvent/minimal", "Root/branch/Root.choice/IExamplePhase/PhaseEndedEvent/maximal",
        ], CollectionOrdering.Matching);
        using var nested = JsonDocument.Parse(rows.Single(static row => row.Name.EndsWith("/PhaseEndedEvent/minimal", StringComparison.Ordinal)).Json);
        await Assert.That(nested.RootElement.GetProperty("choice").GetProperty("type").GetString()).IsEqualTo("phase");
        await Assert.That(nested.RootElement.GetProperty("choice").GetProperty("status").GetString()).IsEqualTo("ended");
    }

    [Test]
    public async Task GenerateAsync_Should_Keep_Only_The_Ancestor_List_Arm_For_A_Minimal_Descendant()
    {
        var plan = EmitterPlanFixture.CreateHoistPlan();
        plan = plan with
        {
            Models = [.. plan.Models, Record("Root", Property("items",
            new ListTypeReferencePlan { ElementType = Named("IExampleEvent"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull }, true))]
        };
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, ["Root"]);
        using var nested = JsonDocument.Parse(rows.Single(static row => row.Name.EndsWith("/PhaseEndedEvent/minimal", StringComparison.Ordinal)).Json);
        await Assert.That(nested.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("type").GetString()))
            .IsEquivalentTo((string?[])["phase"], CollectionOrdering.Matching);
        await Assert.That(nested.RootElement.GetProperty("items")[0].GetProperty("status").GetString()).IsEqualTo("ended");
    }

    [Test]
    public async Task GenerateAsync_Should_Use_Only_Legal_Recursive_Exits_And_Name_An_Infinite_Cycle()
    {
        var plan = EmitterPlanFixture.Create() with { Models = [Record("Node", Property("next", Named("Node", true), false))], Unions = [] };
        var optional = await RecordCorpusGenerator.GenerateAsync(plan, ["Node"]);
        using var absent = JsonDocument.Parse(optional[1].Json);
        await Assert.That(absent.RootElement.EnumerateObject().Select(static member => member.Name)).IsEmpty();
        plan = plan with { Models = [Record("Node", Property("next", Named("Node", true), true))] };
        using var nullable = JsonDocument.Parse((await RecordCorpusGenerator.GenerateAsync(plan, ["Node"]))[1].Json);
        await Assert.That(nullable.RootElement.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
        plan = plan with { Models = [Record("Node", Property("next", new ListTypeReferencePlan { ElementType = Named("Node"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull }, true))] };
        using var collection = JsonDocument.Parse((await RecordCorpusGenerator.GenerateAsync(plan, ["Node"]))[1].Json);
        await Assert.That(collection.RootElement.GetProperty("next").GetArrayLength()).IsEqualTo(0);
        plan = plan with { Models = [Record("Node", Property("next", Named("Node"), true))] };
        var error = await Assert.That(async () => await RecordCorpusGenerator.GenerateAsync(plan, ["Node"])).Throws<InvalidOperationException>();
        await Assert.That(error!.Message).IsEqualTo("Required record cycle has no finite exit at 'Node.next'.");
        plan = plan with
        {
            Models = [Record("Parent", Property("value", Named("string"), true), Property("next", Named("Child"), false)),
            Record("Child", Property("parent", Named("Parent"), true))]
        };
        using var mutual = JsonDocument.Parse((await RecordCorpusGenerator.GenerateAsync(plan, ["Parent"]))[1].Json);
        await Assert.That(mutual.RootElement.EnumerateObject().Select(static member => member.Name)).IsEquivalentTo(["value"], CollectionOrdering.Matching);
        await Assert.That(mutual.RootElement.GetProperty("value").GetString()).IsEqualTo("Parent.value");
    }

    [Test]
    public async Task Generate_Should_Name_Ordered_Rows_And_Preserve_Builder_Intent()
    {
        var plan = EmitterPlanFixture.Create() with
        {
            Models = [Record("Root", Property("child", Named("Child"), true)), Record("Child", Property("value", Named("string"), true))],
            Unions = [],
        };
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, ["Root"]);

        await Assert.That(rows.Select(static row => row.Name)).IsEquivalentTo([
            "Root/minimal", "Root/maximal", "Root/missing/child", "Root/null/child", "Root/wrong-token/child",
            "Root/duplicate/child", "Root/null-then-valid/child", "Root/reordered", "Root/escaped",
        ], CollectionOrdering.Matching);
        using var minimal = JsonDocument.Parse(rows[0].Json);
        await Assert.That(minimal.RootElement.EnumerateObject().Select(static property => property.Name))
            .IsEquivalentTo(["child"], CollectionOrdering.Matching);
        await Assert.That(minimal.RootElement.GetProperty("child").GetProperty("value").GetString()).IsEqualTo("Root.child.value");
    }
}
