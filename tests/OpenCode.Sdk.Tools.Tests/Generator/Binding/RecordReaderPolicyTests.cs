using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Binding;

[ClassDataSource<PinnedBinding>(Shared = SharedType.PerTestSession)]
public sealed class RecordReaderPolicyTests(PinnedBinding pinned)
{
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
        var selected = RecordReaderPolicy.Select([open, record], [selectedUnion, otherUnion], out var arms);
        await Assert.That(selected.Keys).IsEquivalentTo(["Selected"], CollectionOrdering.Matching);
        await Assert.That(arms.Keys).IsEquivalentTo(["IOther", "IToolContent"], CollectionOrdering.Matching);
        await Assert.That(arms["IToolContent"]).IsEquivalentTo(["Selected"], CollectionOrdering.Matching);
        await Assert.That(arms["IOther"]).IsEmpty();
    }

    [Test]
    public async Task Select_Should_Refuse_A_Selected_Arm_Without_Its_Bound_Record()
    {
        var exception = await Assert.That(() => RecordReaderPolicy.Select([], [Union("IToolContent", Arm("Missing"))], out _))
            .Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).IsEqualTo("Selected reader arm 'IToolContent.Missing' has no bound object model.");
    }

    [Test]
    public async Task Select_Should_Order_Records_Independently_Of_Variant_Order()
    {
        var selected = RecordReaderPolicy.Select([Record("Zulu"), Record("Alpha")],
            [Union("IToolContent", Arm("Zulu"), Arm("Alpha"))], out var arms);
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
