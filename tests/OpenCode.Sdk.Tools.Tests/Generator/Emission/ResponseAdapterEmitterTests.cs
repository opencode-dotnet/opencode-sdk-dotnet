using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class ResponseAdapterEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_Status_Mapped_Adapters()
    {
        var sources = ResponseAdapterEmitter.Emit(EmitterPlanFixture.CreateClientPlans(), EmitterPlanFixture.Create().Namespaces);

        await Verify(EmitterSnapshot.Create(sources));
    }
}
