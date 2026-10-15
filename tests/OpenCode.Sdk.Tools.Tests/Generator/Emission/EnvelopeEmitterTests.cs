using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class EnvelopeEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_Guarded_Response_Envelopes()
    {
        var sources = EnvelopeEmitter.Emit(EmitterPlanFixture.Create().Clients);

        await Verify(EmitterSnapshot.Create(sources));
    }
}
