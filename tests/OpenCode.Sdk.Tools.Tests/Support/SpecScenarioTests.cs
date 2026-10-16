namespace OpenCode.Sdk.Tools.Tests.Support;

public sealed class SpecScenarioTests
{
    [Test]
    public async Task Build_Should_Write_Spec_To_Mock_FileSystem()
    {
        var context = SpecScenario.Define(_ => { }).Build();

        await Assert.That(context.FileSystem.File.Exists(context.SpecPath)).IsTrue();
        await Assert.That(context.SpecPath).IsEqualTo("spec/openapi.json");
    }

    [Test]
    public async Task FromRawJson_Should_Write_The_Subject_Payload_Unchanged()
    {
        const string rawJson = "{ not json";
        var context = SpecScenario.FromRawJson(rawJson).Build();

        var actual = await context.FileSystem.File.ReadAllTextAsync(
            context.SpecPath,
            CancellationToken.None);

        await Assert.That(actual).IsEqualTo(rawJson);
    }
}
