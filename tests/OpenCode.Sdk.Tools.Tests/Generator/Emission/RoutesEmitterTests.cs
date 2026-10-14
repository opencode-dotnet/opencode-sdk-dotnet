using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class RoutesEmitterTests
{
    [Test]
    public async Task Emit_Should_Produce_Escaped_Route_Builders()
    {
        var plan = EmitterPlanFixture.Create();

        var sources = RoutesEmitter.Emit(plan.Clients, plan.Models);

        await Verify(EmitterSnapshot.Create(sources));
    }
}
