using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class UnionEmitterTests
{
    [Test]
    public async Task Emit_Should_Render_Brand_And_Value_Prose_In_Summaries_And_Prefix_Refusal()
    {
        var union = EmitterPlanFixture.CreateUnionSnapshot()[0] with
        {
            ConceptName = "OAuthEvent",
            Description = null,
            HoistedMembers = [new HoistedMemberPlan { Name = "Value", WireName = "value", IsRequired = false, Type = UnionHoistPlanData.Named("string") }],
        };
        var source = EmitterSnapshot.Create(UnionEmitter.Emit([union], ModelPlacementFixture.SingleSlice([union]), EmitterPlanFixture.Create().Namespaces));
        await Assert.That(source).Contains("Represents an OAuth event union.");
        await Assert.That(source).Contains("Gets the value. Every declared variant carries it");
        await Assert.That(source).Contains("Preserves an unknown OAuth event payload.");
        await Assert.That(source).Contains("The OAuthEvent payload carries the 'rpc.' prefix-tagged arm and is not an unknown OAuth event.");
    }

    [Test]
    public async Task Emit_Should_Inherit_The_Hand_Written_Stream_Cause_Marker()
    {
        var plan = await EmitterPlanFixture.CreateStreamPlanAsync();
        var sources = UnionEmitter.Emit(plan.Unions, plan.Placement, plan.Namespaces);
        var source = EmitterSnapshot.Content(sources, "Example/Models/IStreamFailureCause.cs");

        await Assert.That(source).Contains("public interface IStreamFailureCause : IOpenCodeStreamFailureCause");
        await Assert.That(source).Contains("JsonConverter(typeof(StreamFailureCauseJsonConverter))");
        await Assert.That(source).DoesNotContain("string Tag");
    }

    [Test]
    public async Task Emit_Should_Produce_Tolerant_Marked_Union_Converters()
    {
        var sources = EmitUnionSnapshot();

        await Verify(EmitterSnapshot.Create(sources));
    }

    [Test]
    public async Task Emit_Should_Declare_A_Union_As_An_Interface()
    {
        var source = EmitterSnapshot.Create(EmitUnionSnapshot());

        await Assert.That(source).Contains("public interface IExamplePhase : IExampleEvent");
        await Assert.That(source).DoesNotContain("abstract record");
        await Assert.That(source).Contains("public sealed record UnknownExamplePhase : IExamplePhase");
    }

    [Test]
    public async Task Emit_Should_Leave_Null_To_Outer_Metadata_And_Guard_Foreign_Fixed_Markers()
    {
        var source = EmitterSnapshot.Create(EmitUnionSnapshot());

        var carrier = source[source.IndexOf("class UnknownExamplePhaseJsonConverter", StringComparison.Ordinal)..];
        await Assert.That(carrier).DoesNotContain("HandleNull");
        await Assert.That(carrier).DoesNotContain("payload cannot be null");
        await Assert.That(carrier).Contains("must be 'phase'");
    }

    [Test]
    public async Task Emit_Should_Dispatch_Literal_Tags_Then_The_Prefix_Arm_Then_The_Unknown_Carrier()
    {
        var sources = EmitUnionSnapshot();
        var converter = EmitterSnapshot.Content(sources, "Example/Internal/ExampleEventJsonConverter.cs");

        var known = converter.IndexOf("TryFindKnown(", StringComparison.Ordinal);
        var prefix = converter.IndexOf("marker.StartsWith(\"rpc.\", StringComparison.Ordinal)", StringComparison.Ordinal);
        var unknown = converter.IndexOf("new UnknownExampleEvent(marker, payload)", StringComparison.Ordinal);
        await Assert.That(known).IsGreaterThan(-1);
        await Assert.That(prefix).IsGreaterThan(known);
        await Assert.That(unknown).IsGreaterThan(prefix);
        await Assert.That(converter).Contains("PrefixArm = UnionArm<IExampleEvent>.Of<RpcEvent>()");
        await Assert.That(converter).Contains("return PrefixArm.Read(ref reader)");
        await Assert.That(converter).DoesNotContain("JsonSerializer.Deserialize(ref reader");
    }

    [Test]
    public async Task Emit_Should_Make_The_Unknown_Carrier_Refuse_A_Prefix_Claimed_Marker()
    {
        var sources = EmitUnionSnapshot();
        var carrier = EmitterSnapshot.Content(sources, "Example/Models/UnknownExampleEvent.cs");
        var carrierConverter = EmitterSnapshot.Content(sources, "Example/Internal/UnknownExampleEventJsonConverter.cs");

        await Assert.That(carrier).Contains("The 'type' marker is claimed by the 'rpc.' prefix-tagged arm and cannot be carried as unknown.");
        await Assert.That(carrierConverter).Contains("The ExampleEvent payload carries the 'rpc.' prefix-tagged arm and is not an unknown example event.");
    }

    [Test]
    public async Task Emit_Should_Leave_A_Union_Without_A_Prefix_Arm_Unchanged()
    {
        var sources = EmitUnionSnapshot();
        var phase = EmitterSnapshot.Content(sources, "Example/Internal/ExamplePhaseJsonConverter.cs");

        await Assert.That(phase).DoesNotContain("StartsWith(");
    }

    [Test]
    public async Task Emit_Should_Table_String_Markers_By_Their_Bytes_And_Freeze_Boolean_Markers()
    {
        var unions = EmitterPlanFixture.CreateUnionSnapshot();
        var phase = unions.Single(static union => union.ConceptName == "ExamplePhase");
        IReadOnlyList<UnionPlan> booleanPhase =
        [
            .. unions.Where(static union => union.ConceptName != "ExamplePhase"),
            phase with { MarkerKind = LiteralKind.Boolean, Variants = [phase.Variants[0] with { Tag = "true" }] },
        ];

        var strings = EmitterSnapshot.Create(EmitUnionSnapshot());
        var booleans = EmitterSnapshot.Content(
            UnionEmitter.Emit(booleanPhase, ModelPlacementFixture.SingleSlice(booleanPhase), EmitterPlanFixture.Create().Namespaces),
            "Example/Internal/ExamplePhaseJsonConverter.cs");

        await Assert.That(strings).Contains("private static readonly UnionTagTable<UnionArm<IExampleEvent>> ArmsByTag");
        await Assert.That(strings).DoesNotContain("FrozenDictionary<string");
        await Assert.That(strings).DoesNotContain("using System.Collections.Frozen;");
        await Assert.That(booleans).Contains("private static readonly FrozenDictionary<bool, UnionArm<IExamplePhase>> ArmsByTag");
        await Assert.That(booleans).Contains("using System.Collections.Frozen;");
    }

    /// <summary>Emits the shared union snapshot, placed in one slice built from the same plans.</summary>
    private static IReadOnlyList<GeneratedSource> EmitUnionSnapshot()
    {
        var unions = EmitterPlanFixture.CreateUnionSnapshot();

        return UnionEmitter.Emit(unions, ModelPlacementFixture.SingleSlice(unions), EmitterPlanFixture.Create().Namespaces);
    }
}
