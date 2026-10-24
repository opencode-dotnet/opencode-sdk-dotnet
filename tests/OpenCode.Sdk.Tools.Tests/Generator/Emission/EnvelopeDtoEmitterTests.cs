using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class EnvelopeDtoEmitterTests
{
    [Test]
    public async Task Emit_Should_Render_The_Envelope_Dtos()
    {
        var sources = EnvelopeDtoEmitter.Emit(EmitterPlanFixture.CreateClientPlans(), EmitterPlanFixture.Create().Namespaces);

        await Verify(EmitterSnapshot.Create(sources));
    }
}
