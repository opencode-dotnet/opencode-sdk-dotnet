using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

public sealed class RecordCorpusGeneratorTests
{
    [Test]
    public async Task GenerateAsync_Should_Vary_Nested_Members_Through_An_Arm_Route()
    {
        var rows = await RecordCorpusGenerator.GenerateAsync(RecordReaderPlanFixture.CreateNested(), ["NestedRecord"]);
        await Assert.That(rows.Where(static row => row.Name.StartsWith("ToolContent/nested/child/", StringComparison.Ordinal)).Select(static row => row.Name))
            .IsEquivalentTo([
                "ToolContent/nested/child/missing/value", "ToolContent/nested/child/null/value", "ToolContent/nested/child/wrong-token/value",
                "ToolContent/nested/child/duplicate/value", "ToolContent/nested/child/null-then-valid/value",
                "ToolContent/nested/child/missing/text", "ToolContent/nested/child/null/text", "ToolContent/nested/child/wrong-token/text",
                "ToolContent/nested/child/duplicate/text", "ToolContent/nested/child/null-then-valid/text",
                "ToolContent/nested/child/null/note", "ToolContent/nested/child/wrong-token/note",
                "ToolContent/nested/child/duplicate/note", "ToolContent/nested/child/null-then-valid/note",
                "ToolContent/nested/child/reordered", "ToolContent/nested/child/escaped",
            ], CollectionOrdering.Matching);
        using var duplicate = JsonDocument.Parse(rows.Single(static row => row.Name == "ToolContent/nested/child/duplicate/value").Json);
        await Assert.That(duplicate.RootElement.GetProperty("child").EnumerateObject().Where(static member => member.Name == "value")
            .Select(static member => member.Value.GetInt64()).Distinct()).Count().IsEqualTo(2);
        var escaped = System.Text.Encoding.UTF8.GetString(rows.Single(static row => row.Name == "ToolContent/nested/child/escaped").Json);
        await Assert.That(escaped).Contains("\\u0076");
        await Assert.That(escaped).Contains("\\u004E");
    }

    [Test]
    public async Task GenerateAsync_Should_Use_Distinct_Duplicates_And_Strong_Sentinels()
    {
        var rows = await RecordCorpusGenerator.GenerateAsync(RecordReaderPlanFixture.Create(), ["DenseRecord"]);
        var seeds = rows.Where(static row => row.Name is "ToolContent/dense/minimal" or "ToolContent/dense/maximal")
            .Select(static row => JsonDocument.Parse(row.Json)).ToArray();
        try
        {
            await Assert.That(seeds.Select(static seed => seed.RootElement.GetProperty("flag").GetBoolean()))
                .IsEquivalentTo([false, true], CollectionOrdering.Matching);
            await Assert.That(seeds.Select(static seed => seed.RootElement.GetProperty("mode").GetString()))
                .IsEquivalentTo((string?[])["fast-mode", "safe"], CollectionOrdering.Matching);
            await Assert.That(seeds[1].RootElement.GetProperty("strict").GetDouble()).IsGreaterThan(1000);
        }
        finally
        {
            foreach (var seed in seeds)
            {
                seed.Dispose();
            }
        }
        foreach (var row in rows.Where(static row => row.Name.StartsWith("ToolContent/dense/duplicate/", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(row.Json);
            var key = row.Name.Split('/')[^1];
            var values = document.RootElement.EnumerateObject().Where(member => member.Name == key).ToArray();
            await Assert.That(values.Length).IsEqualTo(2).Because(row.Name);
            await Assert.That(values[0].Value.GetRawText()).IsNotEqualTo(values[1].Value.GetRawText()).Because(row.Name);
        }
        using var missing = JsonDocument.Parse(rows.Single(static row => row.Name == "ToolContent/dense/omit-marker/kind").Json);
        await Assert.That(missing.RootElement.EnumerateObject().Select(static member => member.Name)).DoesNotContain("kind");
        using var nullMarker = JsonDocument.Parse(rows.Single(static row => row.Name == "ToolContent/dense/null-marker/kind").Json);
        await Assert.That(nullMarker.RootElement.GetProperty("kind").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task GenerateAsync_Should_Cover_Every_Record_And_Containing_Union_Pair()
    {
        var plan = RecordReaderPlanFixture.Create();
        var rows = await RecordCorpusGenerator.GenerateAsync(plan, ["DenseRecord", "SiblingRecord"]);
        (string, string?)[] expected = [("DenseRecord", "IToolContent"), ("DenseRecord", "IOther"),
            ("SiblingRecord", "IToolContent"), ("SiblingRecord", "IOther")];
        await Assert.That(rows.Where(static row => row.IsSeed).Select(static row => (row.RootType, row.UnionType)).Distinct())
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(rows.Where(static row => row.IsSeed || row.Name.Contains("/tag/", StringComparison.Ordinal)).Select(static row => row.Name))
            .IsEquivalentTo([
                "ToolContent/dense/minimal", "ToolContent/dense/maximal", "ToolContent/dense/tag/oracle.future",
                "Other/dense/minimal", "Other/dense/maximal", "Other/dense/tag/oracle.future",
                "ToolContent/sibling/minimal", "ToolContent/sibling/maximal", "ToolContent/sibling/tag/oracle.future",
                "Other/sibling/minimal", "Other/sibling/maximal", "Other/sibling/tag/oracle.future",
            ], CollectionOrdering.Matching);
    }

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
    public async Task GenerateAsync_Should_Name_Ordered_Rows_And_Preserve_Builder_Intent()
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
            "Root/child/missing/value", "Root/child/null/value", "Root/child/wrong-token/value", "Root/child/duplicate/value",
            "Root/child/null-then-valid/value", "Root/child/reordered", "Root/child/escaped",
        ], CollectionOrdering.Matching);
        using var minimal = JsonDocument.Parse(rows[0].Json);
        await Assert.That(minimal.RootElement.EnumerateObject().Select(static property => property.Name))
            .IsEquivalentTo(["child"], CollectionOrdering.Matching);
        await Assert.That(minimal.RootElement.GetProperty("child").GetProperty("value").GetString()).IsEqualTo("Root.child.value");
    }
}
