namespace OpenCode.Sdk.Tests.Common.Options;

public sealed class OpenCodeClientOptionsTests
{
    [Test]
    public async Task OpenCodeClientOptions_Should_Default_To_The_Opencode_Username()
    {
        var options = new OpenCodeClientOptions();

        await Assert.That(options.Username).IsEqualTo("opencode");
        await Assert.That(options.Endpoint).IsNull();
        await Assert.That(options.Password).IsNull();
    }
}
