using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class RegistryEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_The_Source_Generated_Context_Registry()
    {
        var sources = RegistryEmitter.Emit(EmitterPlanFixture.CreateRegistry());
        var snapshot = EmitterSnapshot.Create(sources);

        await Assert.That(snapshot).Contains("UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip");
        await Verify(snapshot);
    }

    [Test]
    public async Task Emit_Should_Register_A_Bare_List_Payload_With_A_Pinned_Accessor_Name()
    {
        var plan = new RegistryPlan
        {
            TypeNames = ["WidgetInfo"],
            PayloadEntries =
            [
                new ListTypeReferencePlan
                {
                    ElementType = new NamedTypeReferencePlan
                    {
                        Name = "WidgetInfo",
                        IsNullable = false,
                        JsonNullRepresentation = JsonNullRepresentation.ClrNull,
                    },
                    IsNullable = false,
                    JsonNullRepresentation = JsonNullRepresentation.ClrNull,
                },
            ],
        };
        var source = EmitterSnapshot.Create(RegistryEmitter.Emit(plan));
        await Assert.That(source).Contains(
            "[JsonSerializable(typeof(IReadOnlyList<WidgetInfo>), TypeInfoPropertyName = \"WidgetInfoList\")]");
    }
}
