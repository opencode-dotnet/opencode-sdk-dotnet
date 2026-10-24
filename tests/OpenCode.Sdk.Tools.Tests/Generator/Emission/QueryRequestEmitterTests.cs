using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class QueryRequestEmitterTests
{
    [Test]
    public async Task Emit_Should_Render_The_Query_Request_Records()
    {
        var sources = QueryRequestEmitter.Emit(EmitterPlanFixture.Create().Clients, EmitterPlanFixture.Create().Namespaces);

        await Verify(EmitterSnapshot.Create(sources));
    }
}
