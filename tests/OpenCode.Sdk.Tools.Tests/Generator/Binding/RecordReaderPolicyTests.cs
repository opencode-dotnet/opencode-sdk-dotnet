using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class RecordReaderPolicyTests(PinnedBinding pinned)
{
    [Test]
    public async Task Select_Should_Claim_Nested_Readers_Without_Adding_Union_Arms()
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        await Assert.That(plan.ReaderRecords.Keys).IsEquivalentTo(["DenseRecord", "NestedRecord", "NestedValue", "SiblingRecord"], CollectionOrdering.Matching);
        await Assert.That(plan.ReaderArms["IToolContent"]).IsEquivalentTo(["DenseRecord", "NestedRecord", "SiblingRecord"], CollectionOrdering.Matching);
        var inventory = GeneratedTypeNameInventory.Create(new OwnershipCandidates { Models = plan.Models, Unions = plan.Unions, HoistedInterfaces = [] },
            [], [], new Dictionary<string, string>(StringComparer.Ordinal), plan.ReaderRecords);
        await Assert.That(inventory.Claims.Where(static claim => claim.Name == "NestedValueJsonReader").Select(static claim => claim.Subject))
            .IsEquivalentTo(["NestedValue"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("unselected")]
    [Arguments("scalar")]
    [Arguments("union")]
    [Arguments("open")]
    [Arguments("list")]
    [Arguments("dictionary")]
    public async Task Select_Should_Refuse_An_Invalid_Nested_Call_Site(string shape)
    {
        var plan = RecordReaderPlanFixture.CreateNested();
        var recordName = shape == "unselected" ? "CreatedEvent" : "NestedRecord";
        var memberName = shape switch { "missing" => "Missing", "unselected" => "Item", _ => "Child" };
        TypeReferencePlan type = shape switch
        {
            "scalar" => Named("string"),
            "union" => Named("IToolContent"),
            "list" => new ListTypeReferencePlan { ElementType = Named("NestedValue"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull },
            "dictionary" => new DictionaryTypeReferencePlan { ValueType = Named("NestedValue"), IsNullable = false, JsonNullRepresentation = JsonNullRepresentation.ClrNull },
            _ => Named("NestedValue"),
        };
        var models = plan.Models.Select(model => model switch
        {
            ObjectModelPlan { Name: "NestedRecord" } record => record with
            { Properties = [.. record.Properties.Select(property => property.Name == "Child" ? property with { Type = type } : property)] },
            ObjectModelPlan { Name: "NestedValue" } value when shape == "open" => value with { EmitsExtensionData = true },
            _ => model,
        }).ToArray();
        var sites = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [recordName] = new HashSet<string>(StringComparer.Ordinal) { memberName },
        };
        var exception = await Assert.That(() => RecordReaderPolicy.Select(models, plan.Unions, out _, RecordReaderPlanFixture.Rollout, sites))
            .Throws<BindingException>();
        await Assert.That(exception!.Errors).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, recordName, $"Nested reader call site '{recordName}.{memberName}' must name a direct closed-record member of a selected record."),
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Not_Convert_An_Unlisted_Variant_In_A_Rollout_Union()
    {
        var selected = RecordReaderPolicy.Select([Record("Unlisted"), Record("ToolFileContent")],
            [Union("IToolContent", Arm("Unlisted"), Arm("ToolFileContent"))], out var arms);
        await Assert.That(selected.Keys).IsEquivalentTo(["ToolFileContent"], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["ToolFileContent"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Bind_Should_Carry_The_Six_Readers_Into_Normal_Emission()
    {
        var sources = Tools.Generator.Emission.SourceEmitter.Emit(pinned.Plan);
        await Assert.That(pinned.Plan.ReaderRecords.Keys).IsEquivalentTo([
            "SessionMessageToolStateCompleted", "SessionMessageToolStateError", "SessionMessageToolStateRunning",
            "SessionMessageToolStateStreaming", "ToolFileContent", "ToolTextContent",
        ], CollectionOrdering.Matching);
        await Assert.That(pinned.Plan.ReaderArms.Where(static entry => entry.Value.Count > 0).Select(static entry => entry.Key))
            .IsEquivalentTo(["ISessionMessageToolState", "IToolContent"], CollectionOrdering.Matching);
        await Assert.That(sources.Select(static source => source.RelativePath)
            .Where(static path => path.EndsWith("JsonReader.cs", StringComparison.Ordinal)))
            .IsEquivalentTo([
                "Sessions/Internal/SessionMessageToolStateCompletedJsonReader.cs", "Sessions/Internal/SessionMessageToolStateErrorJsonReader.cs",
                "Sessions/Internal/SessionMessageToolStateRunningJsonReader.cs", "Sessions/Internal/SessionMessageToolStateStreamingJsonReader.cs",
                "Sessions/Internal/ToolFileContentJsonReader.cs", "Sessions/Internal/ToolTextContentJsonReader.cs",
            ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Select_Exactly_The_Two_Rollout_Unions()
    {
        var selected = RecordReaderPolicy.Select(pinned.Plan.Models, pinned.Plan.Unions, out var arms);
        await Assert.That(selected.Keys).IsEquivalentTo([
            "SessionMessageToolStateCompleted", "SessionMessageToolStateError", "SessionMessageToolStateRunning",
            "SessionMessageToolStateStreaming", "ToolFileContent", "ToolTextContent",
        ], CollectionOrdering.Matching);
        await Assert.That(arms.Where(static entry => entry.Value.Count > 0).Select(static entry => entry.Key))
            .IsEquivalentTo(["ISessionMessageToolState", "IToolContent"], CollectionOrdering.Matching);
        await Assert.That(arms["ISessionMessageToolState"]).IsEquivalentTo([
            "SessionMessageToolStateCompleted", "SessionMessageToolStateError", "SessionMessageToolStateRunning",
            "SessionMessageToolStateStreaming",
        ], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["ToolFileContent", "ToolTextContent"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Select_Should_Exclude_Open_Prefix_And_Nested_Arms_And_Keep_Wiring_Per_Union()
    {
        var open = Variant("Open", "open") with { EmitsExtensionData = true };
        var record = Variant("Selected", "selected");
        var selectedUnion = Union("IToolContent", Arm("Selected"), Arm("Open"), NestedArm("INested"),
            Arm("Mismatch") with { MarkerWireName = "other" }) with
        {
            PrefixVariant = new() { TypeName = "Prefix", Prefix = "rpc.", MarkerWireName = "type" },
        };
        var otherUnion = Union("IOther", Arm("Selected"));
        var selected = RecordReaderPolicy.Select([open, record], [selectedUnion, otherUnion], out var arms,
            new HashSet<(string, string)> { ("IToolContent", "Selected"), ("IToolContent", "Open"), ("IToolContent", "INested"), ("IToolContent", "Mismatch") });
        await Assert.That(selected.Keys).IsEquivalentTo(["Selected"], CollectionOrdering.Matching);
        await Assert.That(arms.Keys).IsEquivalentTo(["IOther", "IToolContent"], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["Selected"], CollectionOrdering.Matching);
        await Assert.That(arms["IOther"]).IsEmpty();
    }

    [Test]
    public async Task Select_Should_Refuse_A_Selected_Arm_Without_Its_Bound_Record()
    {
        var exception = await Assert.That(() => RecordReaderPolicy.Select([], [Union("IToolContent", Arm("Missing"))], out _,
                new HashSet<(string, string)> { ("IToolContent", "Missing") }))
            .Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).IsEqualTo("Selected reader arm 'IToolContent.Missing' has no bound object model.");
    }

    [Test]
    public async Task Select_Should_Order_Records_Independently_Of_Variant_Order()
    {
        var selected = RecordReaderPolicy.Select([Record("Zulu"), Record("Alpha")],
            [Union("IToolContent", Arm("Zulu"), Arm("Alpha"))], out var arms,
            new HashSet<(string, string)> { ("IToolContent", "Zulu"), ("IToolContent", "Alpha") });
        await Assert.That(selected.Keys).IsEquivalentTo(["Alpha", "Zulu"], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["Alpha", "Zulu"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Create_Should_Claim_Readers_Only_For_An_Explicit_Selection_And_Refuse_A_Collision()
    {
        var record = Record("Value");
        var candidates = new OwnershipCandidates { Models = [record, Record("ValueJsonReader")], Unions = [], HoistedInterfaces = [] };
        var schemaNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var empty = GeneratedTypeNameInventory.Create(candidates, [], [], schemaNames);
        await Assert.That(empty.Claims.Where(static claim => claim.Role == GeneratedTypeNameInventory.ClaimRole.Derived)
            .Select(static claim => claim.Name)).DoesNotContain("ValueJsonReader");
        var inventory = GeneratedTypeNameInventory.Create(candidates, [], [], schemaNames,
            new Dictionary<string, ObjectModelPlan>(StringComparer.Ordinal) { [record.Name] = record });
        var claim = inventory.Claims.Single(static claim => claim.Name == "ValueJsonReader" && claim.Role == GeneratedTypeNameInventory.ClaimRole.Derived);
        await Assert.That(claim.Subject).IsEqualTo("Value");
        await Assert.That(inventory.OtherClaims(claim).Select(static other => other.Subject))
            .IsEquivalentTo(["ValueJsonReader"], CollectionOrdering.Matching);
        var errors = new BindingErrorCollector();
        _ = OperationPlanBinder.CheckTypeNameCollisions(inventory, [], errors);
        await Assert.That(errors.Snapshot()).IsEquivalentTo([
            new BindingError(BindingErrorCategory.Naming, "ValueJsonReader", "type name 'ValueJsonReader' collides with another generated type"),
        ], CollectionOrdering.Matching);
    }
}
