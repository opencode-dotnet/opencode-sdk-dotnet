using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;
using static OpenCode.Sdk.Tools.Tests.Support.UnionHoistPlanData;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class HoistedMemberEmissionTests
{
    [Test]
    public async Task Emit_Should_Render_The_Carrier_Fallback_And_Value_Grammatically()
    {
        var plan = EmitterPlanFixture.CreateHoistPlan();
        var carrier = plan.HoistedInterfaces[0] with
        {
            Description = null,
            Members = [new HoistedMemberPlan { Name = "Value", WireName = "value", Type = Named("string") }],
        };
        var source = EmitterSnapshot.Create(HoistedInterfaceEmitter.Emit([carrier], plan.Placement));
        await Assert.That(source).Contains("Represents an i durable envelope value.");
        await Assert.That(source).Contains(EmitterPlanFixture.ValueSummary);
    }

    [Test]
    public async Task Emit_Should_Render_The_Hoisted_Carrier_And_Its_Implementations()
    {
        var sources = EmitterSnapshot.Create(SourceEmitter.Emit(EmitterPlanFixture.CreateHoistPlan()));

        await Verify(sources);
    }

    [Test]
    public async Task Emit_Should_Declare_The_Hoisted_Carrier_As_A_Plain_Interface()
    {
        var carrier = HoistedSource("Example/Models/IDurableEnvelope.cs");

        await Assert.That(carrier).Contains("public interface IDurableEnvelope");
        await Assert.That(carrier).Contains("public long Seq { get; }");
        await Assert.That(carrier).Contains("public double Version { get; }");
        // The carrier has no converter and no unknown arm: nothing deserializes into it.
        await Assert.That(carrier).DoesNotContain("JsonConverter");
    }

    [Test]
    public async Task Emit_Should_Declare_Every_Hoisted_Union_Member_Nullable_And_Say_Why()
    {
        var union = HoistedSource("Example/Models/IExampleEvent.cs");

        await Assert.That(union).Contains("public IDurableEnvelope? Durable { get; }");
        await Assert.That(union).Contains("public double? Created { get; }");
        await Assert.That(union).Contains("public string? Id { get; }");
        await Assert.That(union).Contains("null when the payload is an unrecognized variant preserved as");
        await Assert.That(union).Contains("UnknownExampleEvent");
    }

    [Test]
    public async Task Emit_Should_Make_The_Unknown_Carrier_Answer_Null_For_Every_Hoisted_Member_In_Its_Chain()
    {
        var carrier = HoistedSource("Example/Models/UnknownExamplePhase.cs");

        await Assert.That(carrier).Contains("IDurableEnvelope? IExampleEvent.Durable => null;");
        await Assert.That(carrier).Contains("double? IExampleEvent.Created => null;");
        await Assert.That(carrier).Contains("string? IExampleEvent.Id => null;");
        await Assert.That(carrier).Contains("string? IExamplePhase.Phase => null;");
    }

    [Test]
    public async Task Emit_Should_Keep_A_Variant_Property_Concrete_And_Answer_The_Interface_Explicitly()
    {
        var variant = HoistedSource("Example/Models/CreatedEvent.cs");

        await Assert.That(variant).Contains("public required CreatedEventDurable Durable { get; init; }");
        await Assert.That(variant).Contains("IDurableEnvelope? IExampleEvent.Durable => Durable;");
        await Assert.That(variant).Contains("double? IExampleEvent.Created => Created;");
        // A reference type already answers the nullable annotation, so no explicit member appears.
        await Assert.That(variant).DoesNotContain("string? IExampleEvent.Id");
    }

    [Test]
    public async Task Emit_Should_Let_A_Promoted_Record_Implement_The_Carrier_Beside_Its_Own_Identity()
    {
        var promoted = HoistedSource("Example/Models/CreatedEventDurable.cs");

        await Assert.That(promoted).Contains("public sealed record CreatedEventDurable : IDurableEnvelope");
        await Assert.That(promoted).Contains("public required long Seq { get; init; }");
    }

    [Test]
    public async Task Emit_Should_Keep_The_Hoisted_Carrier_Out_Of_The_Serializer_Registry()
    {
        var registry = HoistedSource("Internal/Serialization/OpenCodeJsonContext.cs");

        await Assert.That(registry).DoesNotContain("IDurableEnvelope");
        await Assert.That(registry).Contains("CreatedEventDurable");
    }

    /// <summary>
    /// The rendered half of the tri-state carrier case: the interface declares the unwrapped
    /// member and each record answers it from the wrapper, so the tree compiles.
    /// </summary>
    [Test]
    public async Task Emit_Should_Answer_A_Tristate_Carrier_Member_From_The_Wrapper()
    {
        var sources = SourceEmitter.Emit(HoistedEmitPlan(
            [Union("IExampleEvent", Arm("CreatedEvent"), Arm("DeletedEvent"))],
            [
                Variant("CreatedEvent", "created", Property("durable", Named("CreatedEventDurable"), isRequired: true)),
                Variant("DeletedEvent", "deleted", Property("durable", Named("DeletedEventDurable"), isRequired: true)),
                Record("CreatedEventDurable", Property("note", Named("string", isNullable: true), isRequired: false,
                    emitsOptionalWrapper: true)),
                Record("DeletedEventDurable", Property("note", Named("string", isNullable: true), isRequired: false,
                    emitsOptionalWrapper: true)),
            ],
            Curation(Groups())));

        var carrier = EmitterSnapshot.Content(sources, "Example/Models/IExampleEventDurable.cs");
        var record = EmitterSnapshot.Content(sources, "Example/Models/CreatedEventDurable.cs");

        await Assert.That(carrier).Contains("public string? Note { get; }");
        await Assert.That(record).Contains("public Optional<string?> Note { get; init; }");
        await Assert.That(record).Contains("string? IExampleEventDurable.Note => Note.Value;");
    }

    private static string HoistedSource(string relativePath) =>
        EmitterSnapshot.Content(SourceEmitter.Emit(EmitterPlanFixture.CreateHoistPlan()), relativePath);
}
