using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion;

public sealed class ErrorStyleClassifierTests
{
    [Test]
    public async Task Project_Should_Classify_EffectTag_Error_Style()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("EffectError", schema => schema
            .Type("object")
            .Property("_tag", property => property.Type("string").Enum("EffectError"), required: true)
            .Property("message", property => property.Type("string"), required: true)));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(((ObjectNode)result.Schemas["EffectError"]).ErrorStyle).IsEqualTo(ErrorStyle.EffectTag);
    }

    [Test]
    public async Task Project_Should_Classify_A_Name_Data_Schema_As_No_Error()
    {
        var host = new SchemaProjectionTestHost();
        var scenario = SpecScenario.Define(spec => spec.WithSchema("NamedError", schema => schema
            .Type("object")
            .Property("name", property => property.Type("string").Enum("NamedError"), required: true)
            .Property("data", property => property.Type("object"), required: true)));

        var result = await host.ProjectAsync(scenario);

        await Assert.That(((ObjectNode)result.Schemas["NamedError"]).ErrorStyle).IsEqualTo(ErrorStyle.None);
    }
}
