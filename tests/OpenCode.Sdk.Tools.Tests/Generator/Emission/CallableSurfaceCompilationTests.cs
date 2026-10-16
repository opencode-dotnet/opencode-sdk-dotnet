using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Tests.Generator.Emission.Support;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Emission;

public sealed class CallableSurfaceCompilationTests
{
    [Test]
    [ParallelLimiter<RoslynCompilationSlots>]
    public async Task Emit_Should_Produce_A_Compilable_Callable_Surface()
    {
        var sources = SourceEmitter.Emit(EmitterPlanFixture.Create());

        var diagnostics = await GeneratedSourceCompiler.CompileWithSdkCoreAsync(sources);

        await Assert.That(diagnostics).IsEmpty();
    }
}
